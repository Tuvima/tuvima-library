using System.Text.Json;
using Dapper;
using MediaEngine.Api.Services.Libraries;
using MediaEngine.Contracts.Items;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Deleting titles from the library, over a real data store and real temp folders: the files that go and the ones that never do,
/// the records that must all be gone afterwards, and the one entry the server keeps. Nothing here may touch anything outside the
/// test's own temp folders.
/// </summary>
public sealed class LibraryItemDeletionTests : IDisposable
{
    private readonly LibraryItemDeletionTestWorld _world = new();
    private readonly List<IDisposable> _extraWorlds = [];

    public void Dispose()
    {
        _world.Dispose();
        foreach (var extra in _extraWorlds)
        {
            extra.Dispose();
        }
    }

    // ----- The confirmation check -----

    [Fact]
    public async Task ADeletionWithAStaleFingerprint_IsRefusedWithAFreshPreview_AndNothingChanges()
    {
        var file = _world.WriteFile(_world.Managed, Path.Combine("Dune", "Dune.mkv"));
        var (workId, _) = _world.AddWork("Dune", [file]);
        var old = (await _world.Service.PreviewAsync([workId])).Value!;

        // A new file arrives after the person opened the dialog.
        var second = _world.WriteFile(_world.Managed, Path.Combine("Dune", "Dune.4k.mkv"));
        _world.AddAssetTo(workId, second);

        var result = await _world.Service.DeleteAsync([workId], old.Fingerprint, LibraryItemDeletionTestWorld.Actor);

        Assert.False(result.IsSuccess);
        Assert.Equal(LibraryItemDeletionRefusalKind.Changed, result.Refusal!.Kind);
        Assert.Equal(LibraryItemDeletionService.ChangedMessage, result.Refusal.Message);
        Assert.NotEqual(old.Fingerprint, result.Refusal.FreshPreview!.Fingerprint);
        Assert.Equal(2, result.Refusal.FreshPreview.Files.Count);
        Assert.True(File.Exists(file));
        Assert.True(File.Exists(second));
        Assert.Equal(1, Count("works", "id", workId));
        Assert.Equal(0, ScalarInt("SELECT COUNT(*) FROM excluded_source_files;"));
        Assert.Equal(0, ScalarInt("SELECT COUNT(*) FROM system_activity WHERE action_type = 'MediaRemoved';"));
        Assert.Empty(_world.Events.Published);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-the-fingerprint")]
    public async Task ADeletionWithAMissingOrWrongFingerprint_ChangesNothing(string? fingerprint)
    {
        var file = _world.WriteFile(_world.Managed, "Dune.mkv");
        var (workId, _) = _world.AddWork("Dune", [file]);

        var result = await _world.Service.DeleteAsync([workId], fingerprint, LibraryItemDeletionTestWorld.Actor);

        Assert.Equal(LibraryItemDeletionRefusalKind.Changed, result.Refusal!.Kind);
        Assert.True(File.Exists(file));
        Assert.Equal(1, Count("works", "id", workId));
    }

    [Fact]
    public async Task ADeletionOfATitleThatIsNoLongerThere_IsNotFound()
    {
        var result = await _world.Service.DeleteAsync([Guid.NewGuid()], "x", LibraryItemDeletionTestWorld.Actor);

        Assert.Equal(LibraryItemDeletionRefusalKind.NotFound, result.Refusal!.Kind);
    }

    [Fact]
    public async Task TwoDeletionsOfTheSameTitleAtOnce_DeleteItOnce_AndTheSecondIsNotFound()
    {
        var file = _world.WriteFile(_world.Managed, "Dune.mkv");
        var (workId, _) = _world.AddWork("Dune", [file]);
        var fingerprint = (await _world.Service.PreviewAsync([workId])).Value!.Fingerprint;

        var results = await Task.WhenAll(
            Task.Run(() => _world.Service.DeleteAsync([workId], fingerprint, LibraryItemDeletionTestWorld.Actor)),
            Task.Run(() => _world.Service.DeleteAsync([workId], fingerprint, LibraryItemDeletionTestWorld.Actor)));

        Assert.Single(results, result => result.IsSuccess);
        var loser = Assert.Single(results, result => !result.IsSuccess);
        Assert.Equal(LibraryItemDeletionRefusalKind.NotFound, loser.Refusal!.Kind);
        Assert.Equal(1, ScalarInt("SELECT COUNT(*) FROM system_activity WHERE action_type = 'MediaRemoved';"));
    }

    // ----- Files on disk -----

    [Fact]
    public async Task WithTheRecycleBinOn_TheFileMovesToTheBin_AndCanBePutBack()
    {
        _world.TurnOnMoviesRecycleBin();
        var media = _world.WriteFile(_world.Managed, Path.Combine("Movies", "Dune Part One (2021)", "Dune Part One (2021).mkv"), bytes: 1000);
        var (workId, _) = _world.AddWork("Dune", [media]);
        var bin = new RecycleBinService(_world.DataRoot, NullLogger.Instance);

        var result = (await DeleteAsync(workId)).Value!;

        Assert.False(File.Exists(media));
        Assert.Equal(1, result.FilesDeleted);
        var entry = Assert.Single(bin.List());
        Assert.Equal(media, entry.OriginalPath);
        Assert.Equal(RecycleBinRestoreResult.Restored, bin.Restore(entry.Id));
        Assert.True(File.Exists(media));
    }

    [Fact]
    public async Task AManagedFileAndItsNamedSidecars_AreDeletedFromDisk_AndTheEmptyFolderGoes_ButTheSourceRootStays()
    {
        var folder = Path.Combine(_world.Managed, "Movies", "Dune Part One (2021)");
        var media = _world.WriteFile(_world.Managed, Path.Combine("Movies", "Dune Part One (2021)", "Dune Part One (2021).mkv"), bytes: 1000);
        var subtitle = _world.WriteFile(_world.Managed, Path.Combine("Movies", "Dune Part One (2021)", "Dune Part One (2021).en.srt"), bytes: 50);
        var friend = _world.WriteFile(_world.Managed, Path.Combine("Other", "notes.txt"), bytes: 7);
        var (workId, _) = _world.AddWork("Dune", [media]);

        var result = (await DeleteAsync(workId)).Value!;

        Assert.False(File.Exists(media));
        Assert.False(File.Exists(subtitle));
        Assert.False(Directory.Exists(folder));
        Assert.False(Directory.Exists(Path.Combine(_world.Managed, "Movies")));
        Assert.True(Directory.Exists(_world.Managed));
        Assert.True(File.Exists(friend));
        Assert.Equal(2, result.FilesDeleted);
        Assert.Equal(1050, result.BytesFreed);
        Assert.Equal(0, result.FilesLeftOnDisk);
        Assert.Empty(result.FilesFailed);
        Assert.Equal([workId], result.DeletedWorkIds);
        Assert.Equal(["Dune"], result.Titles);
    }

    [Fact]
    public async Task AFileAtTheRootOfTheSourceFolder_IsDeleted_AndTheSourceFolderIsNeverRemoved()
    {
        var media = _world.WriteFile(_world.Managed, "Dune.mkv");
        var (workId, _) = _world.AddWork("Dune", [media]);

        await DeleteAsync(workId);

        Assert.False(File.Exists(media));
        Assert.True(Directory.Exists(_world.Managed));
    }

    [Fact]
    public async Task AFolderThatStillHoldsAnotherFile_IsNotRemoved()
    {
        var deleted = _world.WriteFile(_world.Managed, Path.Combine("Films", "Dune.mkv"));
        var stays = _world.WriteFile(_world.Managed, Path.Combine("Films", "Arrival.mkv"));
        var (duneId, _) = _world.AddWork("Dune", [deleted]);
        _world.AddWork("Arrival", [stays]);

        await DeleteAsync(duneId);

        Assert.False(File.Exists(deleted));
        Assert.True(File.Exists(stays));
        Assert.True(Directory.Exists(Path.Combine(_world.Managed, "Films")));
    }

    [Fact]
    public async Task AnAsIsFile_AndAMergeOriginal_AreUntouchedOnDisk_AndEachIsRememberedAsExcluded()
    {
        var asIs = _world.WriteFile(_world.AsIs, Path.Combine("Rips", "Dune.2021.mkv"), bytes: 321);
        var original = _world.WriteFile(_world.Managed, Path.Combine("Imports", "Arrival.mkv"), bytes: 123);
        await _world.MergeLedger.UpsertAsync(new FolderOrganiseLogEntry(
            Guid.NewGuid(), _world.ManagedSourceId, original, "x", FolderOrganiseOutcomes.LeftInSource, DateTimeOffset.UtcNow));
        var (duneId, duneAssets) = _world.AddWork("Dune", [asIs]);
        var (arrivalId, arrivalAssets) = _world.AddWork("Arrival", [original]);
        var duneHash = HashOf(duneAssets[0]);
        var arrivalHash = HashOf(arrivalAssets[0]);

        var result = (await DeleteAsync(duneId, arrivalId)).Value!;

        Assert.True(File.Exists(asIs));
        Assert.True(File.Exists(original));
        Assert.Equal(0, result.FilesDeleted);
        Assert.Equal(0, result.BytesFreed);
        Assert.Equal(2, result.FilesLeftOnDisk);
        Assert.Empty(result.FilesFailed);
        Assert.Equal(0, Count("works", "id", duneId) + Count("works", "id", arrivalId));

        var rows = (await _world.Excluded.ListAllAsync()).OrderBy(row => row.Title, StringComparer.Ordinal).ToList();
        Assert.Equal(2, rows.Count);
        var arrival = rows[0];
        Assert.Equal("Arrival", arrival.Title);
        Assert.Equal(ExcludedSourceFileReasons.Protected, arrival.Reason);
        Assert.Equal(_world.ManagedSourceId.ToString("D"), arrival.SourceId);
        Assert.Equal("movies", arrival.LibraryId);
        Assert.Equal("Imports/Arrival.mkv", arrival.RelativePath);
        Assert.Equal(arrivalHash, arrival.ContentHash);
        Assert.Equal(123, arrival.FileSize);
        Assert.NotNull(arrival.LastWriteUtc);
        var dune = rows[1];
        Assert.Equal("Dune", dune.Title);
        Assert.Equal(ExcludedSourceFileReasons.AsIs, dune.Reason);
        Assert.Equal(_world.AsIsSourceId.ToString("D"), dune.SourceId);
        Assert.Equal("old-rips", dune.LibraryId);
        Assert.Equal("Rips/Dune.2021.mkv", dune.RelativePath);
        Assert.Equal(duneHash, dune.ContentHash);
        Assert.All(rows, row =>
        {
            Assert.Equal("Shy", row.ExcludedByName);
            Assert.Equal(LibraryItemDeletionTestWorld.Actor.AccountId, row.ExcludedByAccountId);
            Assert.True(row.ExcludedAt > DateTimeOffset.UtcNow.AddMinutes(-5));
        });
    }

    [Fact]
    public async Task AFileThatIsMissingFromDisk_GetsNoExcludedRow_AndTheTitleIsStillDeleted()
    {
        var missing = Path.Combine(_world.AsIs, "gone.mkv");
        var (workId, _) = _world.AddWork("Gone", [missing]);

        var result = (await DeleteAsync(workId)).Value!;

        Assert.Equal(0, ScalarInt("SELECT COUNT(*) FROM excluded_source_files;"));
        Assert.Equal(0, result.FilesDeleted);
        Assert.Equal(0, result.FilesLeftOnDisk);
        Assert.Equal(0, Count("works", "id", workId));
    }

    [Fact]
    public async Task AFileThatAnotherTitleAlsoUses_IsLeftAlone_AndIsNotExcluded()
    {
        var shared = _world.WriteFile(_world.Managed, "Shared.mkv");
        var (first, _) = _world.AddWork("First", [shared]);
        _world.AddWork("Second", [shared]);

        await DeleteAsync(first);

        Assert.True(File.Exists(shared));
        Assert.Equal(0, ScalarInt("SELECT COUNT(*) FROM excluded_source_files;"));
    }

    [Fact]
    public async Task AFileHeldOpenByAnotherProgram_StaysOnDisk_TheTitleIsStillDeleted_AndTheFileIsExcluded()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Only Windows refuses to delete a file another program holds open without share-delete.
            return;
        }

        var locked = _world.WriteFile(_world.Managed, Path.Combine("Dune", "Dune.mkv"));
        var sidecar = _world.WriteFile(_world.Managed, Path.Combine("Dune", "Dune.en.srt"));
        var (workId, _) = _world.AddWork("Dune", [locked]);
        var fingerprint = (await _world.Service.PreviewAsync([workId])).Value!.Fingerprint;

        LibraryItemDeletionResultDto result;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = (await _world.Service.DeleteAsync([workId], fingerprint, LibraryItemDeletionTestWorld.Actor)).Value!;
        }

        Assert.True(File.Exists(locked));
        Assert.True(File.Exists(sidecar), "A sidecar stays beside the media file that could not be removed.");
        Assert.Equal(0, Count("works", "id", workId));
        Assert.Equal(0, result.FilesDeleted);
        var failed = Assert.Single(result.FilesFailed);
        Assert.Equal("Dune.mkv", failed.FileName);
        Assert.Equal("Movies › Dune", failed.FolderLabel);
        Assert.Equal(LibraryItemDeletionService.FileInUseReason, failed.Reason);
        Assert.Equal(2, result.FilesLeftOnDisk);
        var excluded = Assert.Single(await _world.Excluded.ListAllAsync());
        Assert.Equal(ExcludedSourceFileReasons.DeleteFailed, excluded.Reason);
        Assert.Equal("Dune/Dune.mkv", excluded.RelativePath);
    }

    [Fact]
    public async Task AFileTheGateRefusesAtTheMomentOfDeleting_StaysOnDisk_IsReported_AndIsExcluded()
    {
        // The gate allows the file while the plan is made and refuses it when the delete is about to happen, as it would if the
        // folder were switched to as-is or a merge listed the file in between.
        // Two looks are allowed: the preview and the plan the delete makes again. The third, just before the file goes, refuses.
        using var world = new LibraryItemDeletionTestWorld(real => new LateRefusalGate(real, allowedLooks: 2));
        var media = world.WriteFile(world.Managed, Path.Combine("Dune", "Dune.mkv"), bytes: 400);
        var subtitle = world.WriteFile(world.Managed, Path.Combine("Dune", "Dune.en.srt"), bytes: 10);
        var (workId, _) = world.AddWork("Dune", [media]);
        var fingerprint = (await world.Service.PreviewAsync([workId])).Value!.Fingerprint;

        var result = (await world.Service.DeleteAsync([workId], fingerprint, LibraryItemDeletionTestWorld.Actor)).Value!;

        Assert.True(File.Exists(media));
        Assert.True(File.Exists(subtitle));
        Assert.Equal(0, result.FilesDeleted);
        Assert.Equal(0, result.BytesFreed);
        var failed = Assert.Single(result.FilesFailed);
        Assert.Equal(LibraryItemDeletionService.FileNotAllowedReason, failed.Reason);
        var excluded = Assert.Single(await world.Excluded.ListAllAsync());
        Assert.Equal(ExcludedSourceFileReasons.DeleteFailed, excluded.Reason);
        using var connection = world.Database.CreateConnection();
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE id = @workId;", new { workId }));
    }

    [Fact]
    public async Task AFolderTheGateWouldNotLetTuvimaDelete_IsLeftInPlace_EvenWhenItIsEmpty()
    {
        using var world = new LibraryItemDeletionTestWorld(real => new DenyDirectoriesGate(real));
        var media = world.WriteFile(world.Managed, Path.Combine("Films", "Dune.mkv"));
        var (workId, _) = world.AddWork("Dune", [media]);
        var fingerprint = (await world.Service.PreviewAsync([workId])).Value!.Fingerprint;

        await world.Service.DeleteAsync([workId], fingerprint, LibraryItemDeletionTestWorld.Actor);

        Assert.False(File.Exists(media));
        Assert.True(Directory.Exists(Path.Combine(world.Managed, "Films")));
    }

    // ----- Records -----

    [Fact]
    public async Task NoRowForTheDeletedTitle_ItsEditionsOrItsFiles_IsLeftInAnyTable()
    {
        var media = _world.WriteFile(_world.Managed, Path.Combine("Dune", "Dune.mkv"));
        var (workId, assetIds) = _world.AddWork("Dune", [media]);
        var assetId = assetIds[0];
        var editionId = ScalarGuid("SELECT id FROM editions WHERE work_id = @workId;", new { workId });
        var alex = _world.AddProfile("Alex");
        SeedEverythingFor(workId, editionId, assetId, alex);
        var personalActivityBefore = ScalarInt("SELECT COUNT(*) FROM personal_activity;");

        await DeleteAsync(workId);

        var leftovers = RowsHoldingAny([workId, editionId, assetId]);
        Assert.Empty(leftovers);
        // personal_activity is exempt from the walk (Settings revamp D25) and must have survived.
        Assert.Equal(personalActivityBefore, ScalarInt("SELECT COUNT(*) FROM personal_activity;"));
        Assert.Equal(1, ScalarInt("SELECT COUNT(*) FROM personal_activity WHERE work_id = @workId;", new { workId }));
        // The one new MediaRemoved entry carries no entity id, so it holds no key to a deleted row either.
        Assert.Equal(1, ScalarInt("SELECT COUNT(*) FROM system_activity WHERE action_type = 'MediaRemoved' AND entity_id IS NULL;"));
    }

    [Fact]
    public async Task AnotherTitlesRecords_AreNotTouched()
    {
        var media = _world.WriteFile(_world.Managed, "Dune.mkv");
        var other = _world.WriteFile(_world.Managed, "Arrival.mkv");
        var (duneId, duneAssets) = _world.AddWork("Dune", [media]);
        var (arrivalId, arrivalAssets) = _world.AddWork("Arrival", [other]);
        var alex = _world.AddProfile("Alex");
        var bea = _world.AddProfile("Bea");
        var duneEdition = ScalarGuid("SELECT id FROM editions WHERE work_id = @duneId;", new { duneId });
        var arrivalEdition = ScalarGuid("SELECT id FROM editions WHERE work_id = @arrivalId;", new { arrivalId });
        SeedEverythingFor(duneId, duneEdition, duneAssets[0], alex);
        SeedEverythingFor(arrivalId, arrivalEdition, arrivalAssets[0], bea);
        var before = RowCountsHolding([arrivalId, arrivalEdition, arrivalAssets[0]]);

        await DeleteAsync(duneId);

        Assert.Equal(before, RowCountsHolding([arrivalId, arrivalEdition, arrivalAssets[0]]));
        Assert.True(File.Exists(other));
        Assert.Equal(1, Count("works", "id", arrivalId));
    }

    [Fact]
    public async Task EveryProfilesStateForTheTitle_IsRemoved_AndAnotherTitlesStateIsKept()
    {
        var media = _world.WriteFile(_world.Managed, "Dune.mkv");
        var other = _world.WriteFile(_world.Managed, "Arrival.mkv");
        var (duneId, duneAssets) = _world.AddWork("Dune", [media]);
        var (arrivalId, arrivalAssets) = _world.AddWork("Arrival", [other]);
        var alex = _world.AddProfile("Alex");
        var bea = _world.AddProfile("Bea");
        var now = DateTimeOffset.UtcNow.ToString("O");
        using (var connection = _world.Database.CreateConnection())
        {
            foreach (var (profile, assetId, workId) in new[]
                     {
                         (alex, duneAssets[0], duneId), (bea, duneAssets[0], duneId),
                         (alex, arrivalAssets[0], arrivalId),
                     })
            {
                connection.Execute("INSERT OR REPLACE INTO user_states (user_id, asset_id, progress_pct) VALUES (@profile, @assetId, 40);", new { profile, assetId });
                connection.Execute("""
                    INSERT INTO consumption_history (id, profile_id, asset_id, session_key, experience, started_at, updated_at, progress_pct)
                    VALUES (@id, @profile, @assetId, 's', 'watch', @now, @now, 40);
                    """, new { id = Guid.NewGuid(), profile, assetId, now });
                connection.Execute("""
                    INSERT INTO reader_bookmarks (id, user_id, asset_id, chapter_index) VALUES (@id, @userId, @assetId, 3);
                    """, new { id = Guid.NewGuid(), userId = profile.ToString("D"), assetId });
                connection.Execute("""
                    INSERT INTO profile_work_preferences (profile_id, work_id, updated_at) VALUES (@profile, @workId, @now);
                    """, new { profile, workId, now });
                connection.Execute("""
                    INSERT INTO music_play_stats (profile_id, work_id, last_played_at) VALUES (@profile, @workId, @now);
                    """, new { profile, workId, now });
                connection.Execute("""
                    INSERT INTO profile_saved_items (profile_id, entity_kind, entity_id, saved_at) VALUES (@profile, 'Movie', @workId, @now);
                    """, new { profile, workId, now });
                connection.Execute("""
                    INSERT INTO profile_reactions (profile_id, entity_kind, entity_id, reaction, updated_at) VALUES (@profile, 'Movie', @workId, 'Like', @now);
                    """, new { profile, workId, now });
            }
        }

        await DeleteAsync(duneId);

        string[] tables =
        [
            "user_states", "consumption_history", "reader_bookmarks", "profile_work_preferences",
            "music_play_stats", "profile_saved_items", "profile_reactions",
        ];
        foreach (var table in tables)
        {
            // Only the other title's single row for Alex is left in each.
            Assert.Equal(1, ScalarInt($"SELECT COUNT(*) FROM {table};"));
        }
    }

    [Fact]
    public async Task AnEmptyCuratedCollection_IsDeletedWithItsActivityEntry_ButSmartAndStillUsedCollectionsAreKept()
    {
        var media = _world.WriteFile(_world.Managed, "Dune.mkv");
        var other = _world.WriteFile(_world.Managed, "Arrival.mkv");
        var (duneId, _) = _world.AddWork("Dune", [media]);
        var (arrivalId, _) = _world.AddWork("Arrival", [other]);
        var roadTrip = _world.AddCollection("Road Trip", manual: true, duneId);
        var comfort = _world.AddCollection("Comfort Films", manual: true, duneId, arrivalId);
        var smart = _world.AddCollection("Recent", manual: false, duneId);
        var playlist = _world.AddCollection("A playlist", manual: true, "Playlist", duneId);

        var result = (await DeleteAsync(duneId)).Value!;

        var removed = Assert.Single(result.CollectionsDeleted);
        Assert.Equal(roadTrip, removed.CollectionId);
        Assert.Equal("Road Trip", removed.Name);
        Assert.Equal(0, Count("collections", "id", roadTrip));
        Assert.Equal(1, Count("collections", "id", comfort));
        Assert.Equal(1, Count("collections", "id", smart));
        Assert.Equal(1, Count("collections", "id", playlist));
        Assert.Equal(1, ScalarInt("SELECT COUNT(*) FROM collection_items WHERE collection_id = @comfort;", new { comfort }));
        Assert.Equal(1, ScalarInt(
            "SELECT COUNT(*) FROM system_activity WHERE action_type = 'AutoPurge' AND detail LIKE 'Removed the collection ''Road Trip''%';"));
    }

    [Fact]
    public async Task IdentityJobsForTheTitleAndItsFiles_AreRemoved_AndAnotherTitlesJobIsKept()
    {
        var media = _world.WriteFile(_world.Managed, "Dune.mkv");
        var other = _world.WriteFile(_world.Managed, "Arrival.mkv");
        var (duneId, duneAssets) = _world.AddWork("Dune", [media]);
        var (_, arrivalAssets) = _world.AddWork("Arrival", [other]);
        _world.AddIdentityJob(duneAssets[0], "RetailSearching");
        _world.AddIdentityJob(duneId, "Queued");
        _world.AddIdentityJob(arrivalAssets[0], "Queued");
        Assert.True((await _world.Service.PreviewAsync([duneId])).Value!.ProcessingRunning);

        await DeleteAsync(duneId);

        Assert.Equal(0, ScalarInt("SELECT COUNT(*) FROM identity_jobs WHERE entity_id IN (@a, @b);", new { a = duneAssets[0], b = duneId }));
        Assert.Equal(1, ScalarInt("SELECT COUNT(*) FROM identity_jobs WHERE entity_id = @a;", new { a = arrivalAssets[0] }));
    }

    [Fact]
    public async Task ProcessingIsStopped_BeforeTheFilesGo_SoNoWorkerCanLeaseTheJobAgain()
    {
        var media = _world.WriteFile(_world.Managed, "Dune.mkv");
        var (duneId, duneAssets) = _world.AddWork("Dune", [media]);
        _world.AddIdentityJob(duneAssets[0], "Queued");
        _world.AddIdentityJob(duneId, "RetailSearching");

        await new LibraryItemDeletionRepositoryAccess(_world).CancelAsync(duneId);

        Assert.Equal(2, ScalarInt("SELECT COUNT(*) FROM identity_jobs WHERE state = 'Failed' AND lease_owner IS NULL;"));
    }

    [Fact]
    public async Task APictureFileAnotherTitlesArtworkRowStillServes_IsNotDeleted_ButOneNothingReferencesIs()
    {
        // Artwork rows are content-addressed: the first title to need a picture names its file, and a second title with the
        // same picture links to that same row. Deleting the first title must not take the file away from the second.
        var media = _world.WriteFile(_world.Managed, "Dune.mkv");
        var other = _world.WriteFile(_world.Managed, "Arrival.mkv");
        var (duneId, _) = _world.AddWork("Dune", [media]);
        var (arrivalId, _) = _world.AddWork("Arrival", [other]);
        var artworkFolder = Path.Combine(_world.DataRoot, "assets", "artwork", "work");
        Directory.CreateDirectory(artworkFolder);
        var sharedFile = Path.Combine(artworkFolder, "shared.jpg");
        var ownFile = Path.Combine(artworkFolder, "dune-only.jpg");
        File.WriteAllText(sharedFile, "x");
        File.WriteAllText(ownFile, "x");
        using (var connection = _world.Database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO entity_assets (id, entity_id, entity_type, asset_type, local_image_path) VALUES (randomblob(16), @duneId, 'Work', 'CoverArt', @sharedFile);",
                new { duneId, sharedFile });
            connection.Execute(
                "INSERT INTO entity_assets (id, entity_id, entity_type, asset_type, local_image_path) VALUES (randomblob(16), @duneId, 'Work', 'Background', @ownFile);",
                new { duneId, ownFile });
        }

        // The row that keeps the first writer's path is linked to the other title only.
        var shared = SeedArtwork(sharedFile, arrivalId, "Work");

        await DeleteAsync(duneId);

        Assert.True(File.Exists(sharedFile), "Another title's artwork row still serves this file.");
        Assert.Equal(1, Count("artwork_assets", "id", shared));
        Assert.Equal(1, ScalarInt("SELECT COUNT(*) FROM entity_artwork_links WHERE artwork_asset_id = @shared AND entity_id = @arrivalId;", new { shared, arrivalId }));
        Assert.False(File.Exists(ownFile), "A file only the deleted title referenced is removed.");
    }

    [Fact]
    public async Task AFolderKeyedByTheDeletedTitle_IsLeftWhole_WhenARemainingRecordStillUsesAFileInIt_ButOthersAreRemoved()
    {
        var media = _world.WriteFile(_world.Managed, "Dune.mkv");
        var other = _world.WriteFile(_world.Managed, "Arrival.mkv");
        var (duneId, duneAssets) = _world.AddWork("Dune", [media]);
        var (arrivalId, _) = _world.AddWork("Arrival", [other]);
        var derived = Path.Combine(_world.DataRoot, "assets", "derived");
        var sharedFolder = Path.Combine(derived, "work", duneId.ToString("D"));
        var plainFolder = Path.Combine(derived, "audio", duneId.ToString("D"));
        var tracksFolder = Path.Combine(_world.DataRoot, "assets", "text-tracks", "subtitles", duneAssets[0].ToString("D"));
        Directory.CreateDirectory(sharedFolder);
        Directory.CreateDirectory(plainFolder);
        Directory.CreateDirectory(tracksFolder);
        var ownFile = Path.Combine(sharedFolder, "own.bin");
        var servedFile = Path.Combine(sharedFolder, "served.jpg");
        var plainFile = Path.Combine(plainFolder, "a.bin");
        var trackFile = Path.Combine(tracksFolder, "en.vtt");
        foreach (var path in new[] { ownFile, servedFile, plainFile, trackFile })
        {
            File.WriteAllText(path, "x");
        }

        // Another title's artwork row names a file inside the first folder, and a person's headshot names the text-track file.
        SeedArtwork(servedFile, arrivalId, "Work");
        using (var connection = _world.Database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO persons (id, name, created_at, local_headshot_path) VALUES (randomblob(16), 'Someone', @now, @trackFile);",
                new { now = DateTimeOffset.UtcNow.ToString("O"), trackFile });
        }

        await DeleteAsync(duneId);

        Assert.True(File.Exists(servedFile));
        Assert.True(File.Exists(ownFile), "The folder is left whole, not trimmed.");
        Assert.True(File.Exists(trackFile));
        Assert.True(Directory.Exists(tracksFolder));
        Assert.False(Directory.Exists(plainFolder), "A folder nothing else uses is removed.");
    }

    [Fact]
    public async Task ARunningJob_DelaysTheDeleteUntilItLetsGo()
    {
        using var world = new LibraryItemDeletionTestWorld(leaseWaitLimit: TimeSpan.FromSeconds(20));
        var media = world.WriteFile(world.Managed, "Dune.mkv");
        var (workId, assetIds) = world.AddWork("Dune", [media]);
        AddLeasedJob(world, assetIds[0]);
        var fingerprint = (await world.Service.PreviewAsync([workId])).Value!.Fingerprint;
        var released = false;
        var releaser = Task.Run(async () =>
        {
            await Task.Delay(700);
            released = true;
            using var connection = world.Database.CreateConnection();
            connection.Execute("UPDATE identity_jobs SET lease_owner = NULL, lease_expires_at = NULL;");
        });

        var started = DateTimeOffset.UtcNow;
        var result = await world.Service.DeleteAsync([workId], fingerprint, LibraryItemDeletionTestWorld.Actor);
        var elapsed = DateTimeOffset.UtcNow - started;
        await releaser;

        Assert.True(result.IsSuccess);
        Assert.True(released, "The delete must not go ahead while the job is still running.");
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(600), $"Waited only {elapsed}.");
        Assert.True(elapsed < TimeSpan.FromSeconds(15), $"Waited {elapsed}, which is the whole limit.");
        Assert.False(File.Exists(media));
        using var check = world.Database.CreateConnection();
        Assert.Equal(0, check.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE id = @workId;", new { workId }));
        Assert.Equal(0, check.ExecuteScalar<int>("SELECT COUNT(*) FROM identity_jobs;"));
    }

    [Fact]
    public async Task AJobThatNeverLetsGo_TimesOut_AndTheDeleteStillCompletes()
    {
        using var world = new LibraryItemDeletionTestWorld(leaseWaitLimit: TimeSpan.FromMilliseconds(800));
        var media = world.WriteFile(world.Managed, "Dune.mkv");
        var (workId, assetIds) = world.AddWork("Dune", [media]);
        AddLeasedJob(world, assetIds[0]);
        var fingerprint = (await world.Service.PreviewAsync([workId])).Value!.Fingerprint;

        var started = DateTimeOffset.UtcNow;
        var result = await world.Service.DeleteAsync([workId], fingerprint, LibraryItemDeletionTestWorld.Actor);
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.True(result.IsSuccess);
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(700), $"Gave up after only {elapsed}.");
        Assert.False(File.Exists(media));
        using var check = world.Database.CreateConnection();
        Assert.Equal(0, check.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE id = @workId;", new { workId }));
        Assert.Equal(0, check.ExecuteScalar<int>("SELECT COUNT(*) FROM identity_jobs;"));
    }

    [Fact]
    public async Task ArtworkOnlyTheTitleUsed_IsDeletedWithItsFiles_ButSharedArtworkAndFilesOutsideTheDataFolderStay()
    {
        var media = _world.WriteFile(_world.Managed, "Dune.mkv");
        var other = _world.WriteFile(_world.Managed, "Arrival.mkv");
        var (duneId, duneAssets) = _world.AddWork("Dune", [media]);
        var (arrivalId, _) = _world.AddWork("Arrival", [other]);
        var ownRoot = Path.Combine(_world.DataRoot, "assets", "artwork", "work", duneId.ToString("D"), "cover");
        Directory.CreateDirectory(ownRoot);
        var ownFile = Path.Combine(ownRoot, "own.jpg");
        var sharedFile = Path.Combine(_world.DataRoot, "assets", "artwork", "shared", "shared.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(sharedFile)!);
        var outsideFile = Path.Combine(_world.Outside, "keep-me.jpg");
        File.WriteAllText(ownFile, "x");
        File.WriteAllText(sharedFile, "x");
        File.WriteAllText(outsideFile, "x");
        var own = SeedArtwork(ownFile, duneId, "Work");
        var shared = SeedArtwork(sharedFile, duneId, "Work");
        LinkArtwork(shared, arrivalId, "Work");
        var outside = SeedArtwork(outsideFile, duneId, "Work");
        var episodeStill = SeedArtwork(Path.Combine(ownRoot, "still.jpg"), duneAssets[0], "Edition");
        var textTracks = Path.Combine(_world.DataRoot, "assets", "text-tracks", "subtitles", duneAssets[0].ToString("D"));
        Directory.CreateDirectory(textTracks);
        File.WriteAllText(Path.Combine(textTracks, "en.srt"), "x");

        await DeleteAsync(duneId);

        Assert.Equal(0, Count("artwork_assets", "id", own));
        Assert.Equal(0, Count("artwork_assets", "id", outside));
        Assert.Equal(0, Count("artwork_assets", "id", episodeStill));
        Assert.Equal(1, Count("artwork_assets", "id", shared));
        Assert.Equal(1, ScalarInt("SELECT COUNT(*) FROM entity_artwork_links WHERE artwork_asset_id = @shared AND entity_id = @arrivalId;", new { shared, arrivalId }));
        Assert.False(File.Exists(ownFile));
        Assert.False(Directory.Exists(Path.GetDirectoryName(Path.GetDirectoryName(ownFile))), "The emptied owner folders go.");
        Assert.True(File.Exists(sharedFile));
        Assert.True(File.Exists(outsideFile), "Only files inside Tuvima's own data folder are ever removed this way.");
        Assert.False(Directory.Exists(textTracks));
        Assert.True(Directory.Exists(Path.Combine(_world.DataRoot, "assets", "artwork")), "The top-level data folders are never removed.");
    }

    // ----- The record and the live update -----

    [Fact]
    public async Task OneActivityEntryIsWritten_WithWhoDeletedWhatAndTheBytesAndCounts()
    {
        var media = _world.WriteFile(_world.Managed, Path.Combine("Dune", "Dune.mkv"), bytes: 2048);
        var asIs = _world.WriteFile(_world.AsIs, "Rip.mkv");
        var (duneId, _) = _world.AddWork("Dune", [media]);
        var (ripId, _) = _world.AddWork("Rip", [asIs]);
        _world.AddProfile("Someone");

        await DeleteAsync(duneId, ripId);

        using var connection = _world.Database.CreateConnection();
        var row = connection.QuerySingle<(string CollectionName, string Detail, string ChangesJson, Guid? ProfileId)>("""
            SELECT collection_name AS CollectionName, detail AS Detail, changes_json AS ChangesJson, profile_id AS ProfileId
            FROM system_activity WHERE action_type = 'MediaRemoved';
            """);
        Assert.Equal("2 movies", row.CollectionName);
        Assert.Equal("Shy deleted 2 movies from the library: 1 file deleted from disk (2 KB freed), 1 file left on disk.", row.Detail);
        Assert.Equal(LibraryItemDeletionTestWorld.Actor.ProfileId, row.ProfileId);
        using var changes = JsonDocument.Parse(row.ChangesJson);
        Assert.Equal(2, changes.RootElement.GetProperty("titles").GetInt32());
        Assert.Equal(1, changes.RootElement.GetProperty("files_deleted").GetInt32());
        Assert.Equal(2048, changes.RootElement.GetProperty("bytes_freed").GetInt64());
        Assert.Equal(1, changes.RootElement.GetProperty("files_left_on_disk").GetInt32());
        Assert.Equal("Shy", changes.RootElement.GetProperty("actor").GetString());
    }

    [Fact]
    public async Task SeveralSongs_AreCountedInTheActivityEntry()
    {
        var song1 = _world.WriteFile(_world.Managed, "one.mp3");
        var song2 = _world.WriteFile(_world.Managed, "two.mp3");
        var (a, _) = _world.AddWork("One", [song1], mediaType: "Music", workKind: "child");
        var (b, _) = _world.AddWork("Two", [song2], mediaType: "Music", workKind: "child");

        await DeleteAsync(a, b);

        using var connection = _world.Database.CreateConnection();
        Assert.Equal("2 songs", connection.ExecuteScalar<string>("SELECT collection_name FROM system_activity WHERE action_type = 'MediaRemoved';"));
    }

    [Fact]
    public async Task ALiveEvent_IsPublished_WithTheWorkAndAssetIdsThatAreGone()
    {
        var media = _world.WriteFile(_world.Managed, "Dune.mkv");
        var (workId, assetIds) = _world.AddWork("Dune", [media]);

        await DeleteAsync(workId);

        var published = Assert.Single(_world.Events.Published);
        Assert.Equal(SignalREvents.LibraryItemsRemoved, published.Name);
        var payload = Assert.IsType<LibraryItemsRemovedEvent>(published.Payload);
        Assert.Equal([workId], payload.WorkIds);
        Assert.Equal(assetIds, payload.AssetIds);
    }

    [Fact]
    public async Task EmptyParentGroups_AreCleanedUp_WhenTheirLastTitleIsDeleted()
    {
        var media = _world.WriteFile(_world.Managed, "pilot.mkv");
        var (showId, _) = _world.AddWork("A show", [], mediaType: "TV", workKind: "parent");
        var (episodeId, _) = _world.AddWork("Pilot", [media], mediaType: "TV", workKind: "child");
        using (var connection = _world.Database.CreateConnection())
        {
            connection.Execute("UPDATE works SET parent_work_id = @showId WHERE id = @episodeId;", new { showId, episodeId });
            // The show has no edition of its own and no hand-picked or personal ties: only its one child holds it up.
            connection.Execute("DELETE FROM editions WHERE work_id = @showId;", new { showId });
        }

        await DeleteAsync(episodeId);

        Assert.Equal(0, Count("works", "id", episodeId));
        Assert.Equal(0, Count("works", "id", showId));
    }

    // ----- Helpers -----

    private async Task<LibraryItemDeletionResult<LibraryItemDeletionResultDto>> DeleteAsync(params Guid[] workIds)
    {
        var preview = await _world.Service.PreviewAsync(workIds);
        Assert.True(preview.IsSuccess, preview.Refusal?.Message);
        var result = await _world.Service.DeleteAsync(workIds, preview.Value!.Fingerprint, LibraryItemDeletionTestWorld.Actor);
        Assert.True(result.IsSuccess, result.Refusal?.Message);
        return result;
    }

    private int Count(string table, string column, Guid id) =>
        ScalarInt($"SELECT COUNT(*) FROM {table} WHERE {column} = @id;", new { id });

    private int ScalarInt(string sql, object? parameters = null)
    {
        using var connection = _world.Database.CreateConnection();
        return connection.ExecuteScalar<int>(sql, parameters);
    }

    private Guid ScalarGuid(string sql, object? parameters = null)
    {
        using var connection = _world.Database.CreateConnection();
        return connection.ExecuteScalar<Guid>(sql, parameters);
    }

    private string HashOf(Guid assetId)
    {
        using var connection = _world.Database.CreateConnection();
        return connection.ExecuteScalar<string>("SELECT content_hash FROM media_assets WHERE id = @assetId;", new { assetId })!;
    }

    /// <summary>A job a worker is running right now: leased, with a lease that has not run out.</summary>
    private static void AddLeasedJob(LibraryItemDeletionTestWorld world, Guid entityId)
    {
        using var connection = world.Database.CreateConnection();
        connection.Execute("""
            INSERT INTO identity_jobs (id, entity_id, entity_type, media_type, state, lease_owner, lease_expires_at)
            VALUES (@id, @entityId, 'MediaAsset', 'Movies', 'RetailSearching', 'worker-1', @expires);
            """,
            new { id = Guid.NewGuid(), entityId, expires = DateTimeOffset.UtcNow.AddMinutes(5).ToString("O") });
    }

    private Guid SeedArtwork(string path, Guid entityId, string entityType)
    {
        var id = Guid.NewGuid();
        using (var connection = _world.Database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO artwork_assets (id, content_hash, original_path) VALUES (@id, @hash, @path);",
                new { id, hash = Guid.NewGuid().ToString("N"), path });
        }

        LinkArtwork(id, entityId, entityType);
        return id;
    }

    private void LinkArtwork(Guid artworkId, Guid entityId, string entityType)
    {
        using var connection = _world.Database.CreateConnection();
        connection.Execute("""
            INSERT INTO entity_artwork_links (id, entity_id, entity_type, artwork_asset_id, role)
            VALUES (@id, @entityId, @entityType, @artworkId, 'Primary');
            """, new { id = Guid.NewGuid(), entityId, entityType, artworkId });
    }

    /// <summary>A row in every table that holds a key to a title, an edition or a file, so the walk afterwards proves each was cleaned.</summary>
    private void SeedEverythingFor(Guid workId, Guid editionId, Guid assetId, Guid profileId)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var provider = ScalarGuid("SELECT id FROM metadata_providers LIMIT 1;");
        var collectionId = _world.AddCollection("Seeded " + workId.ToString("N"), manual: true, workId);
        var eventId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var session = Guid.NewGuid();
        var commit = "token-" + workId.ToString("N");
        var artworkPath = Path.Combine(_world.DataRoot, "assets", "artwork", workId.ToString("N") + ".jpg");
        var artworkId = SeedArtwork(artworkPath, workId, "Work");
        _world.AddIdentityJob(assetId, "Queued");
        using var connection = _world.Database.CreateConnection();
        var p = new
        {
            workId,
            editionId,
            assetId,
            profileId,
            provider,
            collectionId,
            eventId,
            operationId,
            session,
            commit,
            now,
            artworkId,
            userText = profileId.ToString("D"),
            id = Guid.NewGuid(),
        };

        var statements = new[]
        {
            // Chosen values, claims and the identifiers found for each of the three kinds of id.
            "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@assetId, 'title', 'x', @now);",
            "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@editionId, 'title', 'x', @now);",
            "INSERT INTO canonical_value_arrays (entity_id, key, value) VALUES (@workId, 'genre', 'Drama');",
            "INSERT INTO canonical_value_arrays (entity_id, key, value) VALUES (@assetId, 'alternate_title', 'Dune');",
            "INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value) VALUES (@id, @workId, @provider, 'title', 'x');",
            "INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value) VALUES (randomblob(16), @assetId, @provider, 'title', 'x');",
            "INSERT INTO bridge_ids (id, entity_id, id_type, id_value) VALUES (randomblob(16), @workId, 'imdb_id', 'tt1');",
            "INSERT INTO ai_feature_artifacts (entity_id, feature_key, source_provider_id, status, updated_at) VALUES (@workId, 'vibe', @provider, 'Published', @now);",
            "INSERT INTO enrichment_refresh_schedule (entity_type, entity_id, stage, provider_id, policy_key, interval_days, next_due_at, updated_at) VALUES ('Work', @workId, 'Quick', 'tmdb', 'p', 7, @now, @now);",
            "INSERT INTO entity_capability_states (id, entity_id, entity_kind, capability_id, capability_kind, status, created_at, updated_at) VALUES (randomblob(16), @assetId, 'MediaAsset', 'c', 'k', 'done', @now, @now);",
            "INSERT INTO entity_events (id, entity_id, entity_type, event_type, trigger) VALUES (@eventId, @workId, 'Work', 'x', 'y');",
            "INSERT INTO entity_field_changes (id, event_id, entity_id, field) VALUES (randomblob(16), @eventId, @workId, 'title');",
            "INSERT INTO pending_person_signals (id, entity_id, name, role, source, media_type, created_at) VALUES (randomblob(16), @workId, 'n', 'Author', 's', 'Books', @now);",
            "INSERT INTO search_results_cache (entity_id, results_json, searched_at) VALUES (@workId, '[]', @now);",
            "INSERT INTO transaction_log (event_type, entity_type, entity_id) VALUES ('X', 'Work', @workId);",
            "INSERT INTO media_operations (id, operation_type, operation_kind, entity_id, status, position_key, created_at, updated_at, idempotency_key) VALUES (@operationId, 't', 'k', @assetId, 'Queued', 1, @now, @now, @commit);",
            "INSERT INTO media_operation_events (id, operation_id, entity_id, event_type, occurred_at) VALUES (randomblob(16), @operationId, @assetId, 'x', @now);",
            "INSERT INTO deferred_enrichment_queue (id, entity_id, media_type, created_at) VALUES (randomblob(16), @workId, 'Movies', @now);",
            "INSERT INTO artwork_asset_context (artwork_asset_id, entity_id, entity_type, entity_label, search_text) VALUES (@artworkId, @workId, 'Work', 'Dune', 'dune');",
            "INSERT INTO review_queue (id, entity_id, entity_type, trigger) VALUES (randomblob(16), @workId, 'Work', 'LowConfidence');",
            "INSERT INTO entity_assets (id, entity_id, entity_type, asset_type, local_image_path) VALUES (randomblob(16), @workId, 'Work', 'CoverArt', NULL);",
            "INSERT INTO ingestion_log (id, file_path, media_asset_id) VALUES (randomblob(16), 'f', @assetId);",
            "INSERT INTO search_index (entity_id, title) VALUES (@workId, 'Dune');",
            "INSERT INTO profile_saved_items (profile_id, entity_kind, entity_id, saved_at) VALUES (@profileId, 'Movie', @workId, @now);",
            "INSERT INTO profile_reactions (profile_id, entity_kind, entity_id, reaction, updated_at) VALUES (@profileId, 'Song', @assetId, 'Like', @now);",
            "INSERT INTO personal_status_commands (id, profile_id, target_id, media_type, command, changed_at, snapshot_json) VALUES (randomblob(16), @profileId, @workId, 'Movies', 'watched', @now, '{}');",
            "INSERT INTO system_activity (action_type, entity_id, entity_type, collection_name) VALUES ('MediaAdded', @workId, 'Work', 'Dune');",
            "INSERT INTO system_activity (action_type, entity_id, entity_type, collection_name) VALUES ('FileHashed', @assetId, 'MediaAsset', 'Dune');",
            "INSERT INTO audio_fingerprints (asset_id, fingerprint) VALUES (@assetId, randomblob(8));",
            // The media editor's receipts.
            "INSERT INTO media_editor_commits (operation_token, request_hash, asset_id, source_work_id, target_work_id, target_tvdb_episode_id, committed_at) VALUES (@commit, 'h', @assetId, @workId, randomblob(16), 'e', @now);",
            "INSERT INTO media_editor_commit_items (operation_token, asset_id, source_edition_id, source_work_id, target_work_id, source_season_work_id, target_season_work_id) VALUES (@commit, @assetId, @editionId, @workId, @workId, @workId, @workId);",
            // Per-profile state.
            "INSERT INTO user_states (user_id, asset_id, progress_pct) VALUES (@profileId, @assetId, 40);",
            "INSERT INTO consumption_history (id, profile_id, asset_id, session_key, experience, started_at, updated_at, progress_pct) VALUES (randomblob(16), @profileId, @assetId, 's', 'watch', @now, @now, 40);",
            "INSERT INTO reader_bookmarks (id, user_id, asset_id, chapter_index) VALUES (randomblob(16), @userText, @assetId, 1);",
            "INSERT INTO reader_highlights (id, user_id, asset_id, chapter_index, start_offset, end_offset, selected_text) VALUES (randomblob(16), @userText, @assetId, 1, 0, 1, 'x');",
            "INSERT INTO reader_statistics (id, user_id, asset_id) VALUES (randomblob(16), @userText, @assetId);",
            "INSERT INTO profile_work_preferences (profile_id, work_id, updated_at) VALUES (@profileId, @workId, @now);",
            "INSERT INTO music_play_stats (profile_id, work_id, last_played_at) VALUES (@profileId, @workId, @now);",
            "INSERT INTO music_play_active_segments (profile_id, work_id, started_at, last_heartbeat_at) VALUES (@profileId, @workId, @now, @now);",
            "INSERT INTO audiobook_bookmarks (id, profile_id, work_id, asset_id, created_at) VALUES (randomblob(16), @profileId, @workId, @assetId, @now);",
            "INSERT INTO audiobook_listen_history (id, profile_id, work_id, asset_id, title, device_id, client, started_at, ended_at) VALUES (randomblob(16), @profileId, @workId, @assetId, 't', 'd', 'c', @now, @now);",
            "INSERT INTO audiobook_listen_active_segments (profile_id, work_id, asset_id, title, started_at, device_id, client, last_heartbeat_at) VALUES (@profileId, @workId, @assetId, 't', @now, 'd', 'c', @now);",
            "INSERT INTO player_sessions (profile_id, session_id, device_id, client) VALUES (@profileId, @session, 'd', 'c');",
            "INSERT INTO player_queue_items (id, profile_id, position, work_id, asset_id, media_type, title, added_at) VALUES (randomblob(16), @profileId, 1, @workId, @assetId, 'Movies', 't', @now);",
            "INSERT INTO media_asset_coverage (asset_id, work_id, position, source) VALUES (@assetId, @workId, 1, 'manual');",
            "INSERT INTO text_tracks (id, asset_id, kind, provider, source_format, normalized_format, local_path) VALUES (randomblob(16), @assetId, 'Subtitles', 'p', 'srt', 'vtt', 'nowhere.vtt');",
            "INSERT INTO series_manifest_items (id, collection_id, series_qid, item_qid, order_source, last_hydrated_at, linked_work_id) VALUES (randomblob(16), @collectionId, 'Q1', 'Q2', 'wikidata', @now, @workId);",
            // The person's own history keeps its snapshot (Settings revamp D25).
            "INSERT INTO personal_activity (id, profile_id, kind, started_at, media_type, title, work_id, asset_id) VALUES (randomblob(16), @profileId, 'watch', @now, 'Movies', 'Dune', @workId, @assetId);",
        };
        foreach (var sql in statements)
        {
            try
            {
                connection.Execute(sql, p);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Could not seed: " + sql, ex);
            }
        }
    }

    // The columns that can hold the id of a title (Work), an edition or a media asset.
    private static readonly string[] IdColumns =
    [
        "work_id", "edition_id", "asset_id", "media_asset_id", "entity_id", "linked_work_id", "parent_work_id", "target_id",
        "owner_work_id", "source_work_id", "target_work_id", "source_edition_id", "source_season_work_id", "target_season_work_id",
        "artifact_id", "parent_entity_id",
    ];

    /// <summary>Every table.column that still holds one of the ids, found by walking every table in the data store.</summary>
    private IReadOnlyList<string> RowsHoldingAny(IReadOnlyList<Guid> ids) =>
    [
        .. RowCountsHolding(ids)
            // personal_activity is exempt on purpose (Settings revamp D25): a person's own history keeps its own snapshot of the
            // title, with no foreign key, and survives the title being deleted.
            .Where(pair => pair.Value > 0 && !pair.Key.StartsWith("personal_activity.", StringComparison.Ordinal))
            .Select(pair => $"{pair.Key}={pair.Value}"),
    ];

    private SortedDictionary<string, int> RowCountsHolding(IReadOnlyList<Guid> ids)
    {
        using var connection = _world.Database.CreateConnection();
        var virtualTables = connection.Query<string>(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND sql LIKE 'CREATE VIRTUAL TABLE%';").ToList();
        var tables = connection.Query<string>(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;").ToList();
        var blobs = ids.Select(MediaEngine.Storage.GuidSql.ToBlob).ToArray();
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            // The hidden tables a full-text index keeps for itself (search_index_data, _idx, _content, _docsize, _config).
            if (virtualTables.Any(name => table.StartsWith(name + "_", StringComparison.Ordinal)))
            {
                continue;
            }

            var columns = connection.Query<string>($"SELECT name FROM pragma_table_info('{table}');").ToList();
            foreach (var column in columns.Where(column => IdColumns.Contains(column, StringComparer.Ordinal)))
            {
                counts[$"{table}.{column}"] = connection.ExecuteScalar<int>(
                    $"SELECT COUNT(*) FROM \"{table}\" WHERE \"{column}\" IN @blobs;", new { blobs });
            }
        }

        return counts;
    }

    /// <summary>Asks the real repository to cancel processing, the way the service does just before it deletes files.</summary>
    private sealed class LibraryItemDeletionRepositoryAccess(LibraryItemDeletionTestWorld world)
    {
        public Task CancelAsync(params Guid[] workIds) =>
            new MediaEngine.Storage.LibraryItemDeletionRepository(world.Database).CancelProcessingAsync(workIds);
    }

    /// <summary>Allows a delete for the first few times a file is asked about and refuses it every time after, like a folder switched to as-is in between.</summary>
    private sealed class LateRefusalGate(ISourceMutationPolicyGate real, int allowedLooks) : ISourceMutationPolicyGate
    {
        private readonly Dictionary<string, int> _asked = new(StringComparer.OrdinalIgnoreCase);

        public SourceMutationDecision Evaluate(SourceMutationRequest request)
        {
            if (request.Mutation == SourceMutationKind.Delete
                && !string.Equals(Path.GetExtension(request.Path), string.Empty, StringComparison.Ordinal))
            {
                _asked[request.Path] = _asked.GetValueOrDefault(request.Path) + 1;
                if (_asked[request.Path] > allowedLooks)
                {
                    return SourceMutationDecision.Deny("The folder was switched to as-is.");
                }
            }

            return real.Evaluate(request);
        }
    }

    /// <summary>Allows deleting files but never a folder (a path without an extension).</summary>
    private sealed class DenyDirectoriesGate(ISourceMutationPolicyGate real) : ISourceMutationPolicyGate
    {
        public SourceMutationDecision Evaluate(SourceMutationRequest request) =>
            request.Mutation == SourceMutationKind.Delete && string.IsNullOrEmpty(Path.GetExtension(request.Path))
                ? SourceMutationDecision.Deny("Folders are kept.")
                : real.Evaluate(request);
    }
}
