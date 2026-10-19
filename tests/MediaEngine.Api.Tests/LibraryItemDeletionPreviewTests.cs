using System.Text.Json;
using Dapper;
using MediaEngine.Api.Services.Libraries;
using MediaEngine.Contracts.Items;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Tests;

/// <summary>
/// What deleting titles from the library would do, over a real data store and real folders: which files go, which stay and why,
/// the hand-picked collections affected, who has progress, and the fingerprint that ties a confirmation to this exact plan.
/// Nothing in these tests may delete or change a file: a preview only reads.
/// </summary>
public sealed class LibraryItemDeletionPreviewTests : IDisposable
{
    private readonly LibraryItemDeletionTestWorld _world = new();

    public void Dispose() => _world.Dispose();

    // ----- Files and the safety gate -----

    [Fact]
    public async Task AFileInAManagedFolder_IsListedToBeDeleted_WithItsSizeAndFolderLabel()
    {
        var file = _world.WriteFile(_world.Managed, Path.Combine("Dune Part One (2021)", "Dune Part One (2021).mkv"), bytes: 1000);
        var (workId, assetIds) = _world.AddWork("Dune: Part One", [file]);

        var preview = await PreviewAsync(workId);

        var row = Assert.Single(preview.Files);
        Assert.Equal(LibraryItemDeletionOutcomes.Delete, row.Outcome);
        Assert.Null(row.Reason);
        Assert.Equal(1000, row.SizeBytes);
        Assert.Equal(assetIds[0], row.AssetId);
        Assert.Equal(workId, row.WorkId);
        Assert.False(row.IsSidecar);
        Assert.Equal("Movies", row.LibraryName);
        Assert.Equal("Movies › Dune Part One (2021)", row.FolderLabel);
        Assert.Equal("Dune Part One (2021).mkv", row.FileName);
        Assert.Equal(1000, preview.BytesFreed);
        var item = Assert.Single(preview.Items);
        Assert.Equal("Dune: Part One", item.Title);
        Assert.Equal("Movies", item.MediaType);
        Assert.Equal(1, item.FileCount);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task AFileAtTheRootOfItsSourceFolder_IsLabelledWithTheLibraryNameOnly()
    {
        var file = _world.WriteFile(_world.Managed, "Dune.mkv");
        var (workId, _) = _world.AddWork("Dune", [file]);

        var preview = await PreviewAsync(workId);

        Assert.Equal("Movies", Assert.Single(preview.Files).FolderLabel);
    }

    [Fact]
    public async Task AFileInAnAsIsFolder_StaysOnDisk_AndIsLabelledKeepAsIs()
    {
        var file = _world.WriteFile(_world.AsIs, "Dune.2021.mkv", bytes: 500);
        var (workId, _) = _world.AddWork("Dune", [file]);

        var preview = await PreviewAsync(workId);

        var row = Assert.Single(preview.Files);
        Assert.Equal(LibraryItemDeletionOutcomes.KeepAsIs, row.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(row.Reason));
        Assert.Equal("Old Rips", row.LibraryName);
        Assert.Equal(500, row.SizeBytes);
        Assert.Equal(0, preview.BytesFreed);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task AManagedFileThatAMergeLeftInPlace_IsKeepProtected_AndTheMergeReasonIsShown()
    {
        var file = _world.WriteFile(_world.Managed, "Dune.mkv");
        var (workId, _) = _world.AddWork("Dune", [file]);
        await _world.MergeLedger.UpsertAsync(new FolderOrganiseLogEntry(
            Guid.NewGuid(), _world.ManagedSourceId, file, Path.Combine(_world.Managed, "elsewhere", "Dune.mkv"),
            FolderOrganiseOutcomes.Copied, DateTimeOffset.UtcNow));

        var preview = await PreviewAsync(workId);

        var row = Assert.Single(preview.Files);
        Assert.Equal(LibraryItemDeletionOutcomes.KeepProtected, row.Outcome);
        Assert.Contains("merge", row.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, preview.BytesFreed);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task AFileOutsideEveryLibraryFolder_IsKeepProtected_AndNeverDeleted()
    {
        var file = _world.WriteFile(_world.Outside, "Stray.mkv");
        var (workId, _) = _world.AddWork("Stray", [file]);

        var preview = await PreviewAsync(workId);

        var row = Assert.Single(preview.Files);
        Assert.Equal(LibraryItemDeletionOutcomes.KeepProtected, row.Outcome);
        Assert.Equal(LibraryItemDeletionService.OutsideLibraryReason, row.Reason);
        Assert.Equal(string.Empty, row.LibraryName);
        Assert.Equal("outside", row.FolderLabel);
    }

    [Fact]
    public async Task AFileThatIsMissingFromDisk_IsKeepMissing_WithNoSize()
    {
        var (workId, _) = _world.AddWork("Gone", [Path.Combine(_world.Managed, "Gone.mkv")]);

        var preview = await PreviewAsync(workId);

        var row = Assert.Single(preview.Files);
        Assert.Equal(LibraryItemDeletionOutcomes.KeepMissing, row.Outcome);
        Assert.Null(row.SizeBytes);
        Assert.Equal(0, preview.BytesFreed);
    }

    [Fact]
    public async Task ATitleWithNoFiles_StillPreviews()
    {
        var (workId, _) = _world.AddWork("Never had a file", []);

        var preview = await PreviewAsync(workId);

        Assert.Empty(preview.Files);
        var item = Assert.Single(preview.Items);
        Assert.Equal("Never had a file", item.Title);
        Assert.Equal(0, item.FileCount);
    }

    [Fact]
    public async Task AFileThatAnotherTitleOutsideTheRequestAlsoUses_IsKeepProtected()
    {
        var file = _world.WriteFile(_world.Managed, "Shared.mkv");
        var (requested, _) = _world.AddWork("Requested", [file]);
        _world.AddWork("Someone else's", [file]);

        var preview = await PreviewAsync(requested);

        var row = Assert.Single(preview.Files);
        Assert.Equal(LibraryItemDeletionOutcomes.KeepProtected, row.Outcome);
        Assert.Equal(LibraryItemDeletionService.UsedElsewhereReason, row.Reason);
    }

    [Fact]
    public async Task APreview_NeverPutsAFullDiskPathOnTheWire()
    {
        var file = _world.WriteFile(_world.Managed, Path.Combine("Dune Part One (2021)", "Dune Part One (2021).mkv"));
        _world.WriteFile(_world.Managed, Path.Combine("Dune Part One (2021)", "Dune Part One (2021).en.srt"));
        var (workId, _) = _world.AddWork("Dune", [file]);

        var json = JsonSerializer.Serialize(await PreviewAsync(workId));

        // Every test folder lives under a uniquely named root, so any full path would carry this name.
        Assert.DoesNotContain(Path.GetFileName(_world.Root), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar).Replace("\\", "\\\\", StringComparison.Ordinal), json, StringComparison.OrdinalIgnoreCase);
    }

    // ----- Sidecars -----

    [Fact]
    public async Task ASubtitleNamedAfterADeletedFile_IsListedAsASidecarToDelete()
    {
        var folder = "Dune Part One (2021)";
        var media = _world.WriteFile(_world.Managed, Path.Combine(folder, "Dune Part One (2021).mkv"), bytes: 1000);
        _world.WriteFile(_world.Managed, Path.Combine(folder, "Dune Part One (2021).en.srt"), bytes: 50);
        _world.WriteFile(_world.Managed, Path.Combine(folder, "Dune Part One (2021).nfo"), bytes: 20);
        _world.WriteFile(_world.Managed, Path.Combine(folder, "notes from my friend.txt"), bytes: 7);
        var (workId, _) = _world.AddWork("Dune", [media]);

        var preview = await PreviewAsync(workId);

        Assert.Equal(
            ["Dune Part One (2021).en.srt", "Dune Part One (2021).mkv", "Dune Part One (2021).nfo"],
            preview.Files.Select(file => file.FileName).Order(StringComparer.Ordinal));
        var sidecars = preview.Files.Where(file => file.IsSidecar).ToList();
        Assert.Equal(2, sidecars.Count);
        Assert.All(sidecars, sidecar =>
        {
            Assert.Equal(LibraryItemDeletionOutcomes.Delete, sidecar.Outcome);
            Assert.Null(sidecar.AssetId);
            Assert.Equal(workId, sidecar.WorkId);
            Assert.Equal("Movies › " + folder, sidecar.FolderLabel);
        });
        Assert.Equal(1000 + 50 + 20, preview.BytesFreed);
        Assert.DoesNotContain(preview.Files, file => file.FileName.StartsWith("notes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFolderWidePoster_IsListedWhenNoOtherMediaIsLeftInTheFolder()
    {
        var media = _world.WriteFile(_world.Managed, Path.Combine("Dune", "Dune.mkv"));
        _world.WriteFile(_world.Managed, Path.Combine("Dune", "poster.jpg"), bytes: 30);
        var (workId, _) = _world.AddWork("Dune", [media]);

        var preview = await PreviewAsync(workId);

        var poster = Assert.Single(preview.Files, file => file.FileName == "poster.jpg");
        Assert.True(poster.IsSidecar);
        Assert.Equal(LibraryItemDeletionOutcomes.Delete, poster.Outcome);
    }

    [Fact]
    public async Task AFolderWidePoster_IsLeftAloneWhenAnotherMediaFileStaysInTheFolder()
    {
        var media = _world.WriteFile(_world.Managed, Path.Combine("Films", "Dune.mkv"));
        _world.WriteFile(_world.Managed, Path.Combine("Films", "Arrival.mkv"));
        _world.WriteFile(_world.Managed, Path.Combine("Films", "poster.jpg"));
        _world.WriteFile(_world.Managed, Path.Combine("Films", "Dune-poster.jpg"));
        var (workId, _) = _world.AddWork("Dune", [media]);

        var preview = await PreviewAsync(workId);

        Assert.DoesNotContain(preview.Files, file => file.FileName == "poster.jpg");
        // The picture named after this file still goes with it.
        Assert.Contains(preview.Files, file => file.FileName == "Dune-poster.jpg" && file.Outcome == LibraryItemDeletionOutcomes.Delete);
    }

    [Fact]
    public async Task AFolderWidePoster_IsListedWhenEveryMediaFileInTheFolderIsInTheRequestAndDeleted()
    {
        var first = _world.WriteFile(_world.Managed, Path.Combine("Films", "Dune.mkv"));
        var second = _world.WriteFile(_world.Managed, Path.Combine("Films", "Arrival.mkv"));
        _world.WriteFile(_world.Managed, Path.Combine("Films", "poster.jpg"));
        var (duneId, _) = _world.AddWork("Dune", [first]);
        var (arrivalId, _) = _world.AddWork("Arrival", [second]);

        var preview = await PreviewAsync(duneId, arrivalId);

        Assert.Single(preview.Files, file => file.FileName == "poster.jpg");
    }

    [Fact]
    public async Task AFolderWidePoster_IsLeftAloneWhenAnotherFileInTheRequestStaysOnDisk()
    {
        var managed = _world.WriteFile(_world.Managed, Path.Combine("Films", "Dune.mkv"));
        var mergeOriginal = _world.WriteFile(_world.Managed, Path.Combine("Films", "Arrival.mkv"));
        _world.WriteFile(_world.Managed, Path.Combine("Films", "poster.jpg"));
        await _world.MergeLedger.UpsertAsync(new FolderOrganiseLogEntry(
            Guid.NewGuid(), _world.ManagedSourceId, mergeOriginal, "x", FolderOrganiseOutcomes.LeftInSource, DateTimeOffset.UtcNow));
        var (duneId, _) = _world.AddWork("Dune", [managed]);
        var (arrivalId, _) = _world.AddWork("Arrival", [mergeOriginal]);

        var preview = await PreviewAsync(duneId, arrivalId);

        Assert.DoesNotContain(preview.Files, file => file.FileName == "poster.jpg");
    }

    [Fact]
    public async Task TheSidecarsOfAFileThatStays_AreNotListed()
    {
        var media = _world.WriteFile(_world.AsIs, "Dune.mkv");
        _world.WriteFile(_world.AsIs, "Dune.en.srt");
        _world.WriteFile(_world.AsIs, "poster.jpg");
        var (workId, _) = _world.AddWork("Dune", [media]);

        var preview = await PreviewAsync(workId);

        Assert.Equal("Dune.mkv", Assert.Single(preview.Files).FileName);
    }

    [Fact]
    public async Task ASidecarThatIsTheFileOfATitleOutsideTheRequest_IsNeverListed()
    {
        var media = _world.WriteFile(_world.Managed, Path.Combine("Dune", "Dune.mkv"));
        var picture = _world.WriteFile(_world.Managed, Path.Combine("Dune", "Dune.jpg"));
        var (workId, _) = _world.AddWork("Dune", [media]);
        _world.AddWork("A picture that is an item of its own", [picture]);

        var preview = await PreviewAsync(workId);

        Assert.DoesNotContain(preview.Files, file => file.FileName == "Dune.jpg");
    }

    [Fact]
    public async Task ASidecarSharedByTwoRequestedTitles_IsListedOnce()
    {
        var first = _world.WriteFile(_world.Managed, Path.Combine("Both", "Dune.mkv"));
        var second = _world.WriteFile(_world.Managed, Path.Combine("Both", "Dune.mp4"));
        _world.WriteFile(_world.Managed, Path.Combine("Both", "Dune.en.srt"));
        var (firstId, _) = _world.AddWork("Dune cut one", [first]);
        var (secondId, _) = _world.AddWork("Dune cut two", [second]);

        var preview = await PreviewAsync(firstId, secondId);

        Assert.Single(preview.Files, file => file.FileName == "Dune.en.srt");
    }

    // ----- Hand-picked collections -----

    [Fact]
    public async Task OnlyHandPickedCollections_AreListed_SortedByName_AndTheLastMemberIsFlagged()
    {
        var (dune, _) = _world.AddWork("Dune", []);
        var (arrival, _) = _world.AddWork("Arrival", []);
        _world.AddCollection("road trip", manual: true, dune);
        _world.AddCollection("Comfort Films", manual: true, dune, arrival);
        _world.AddCollection("Zebra night", manual: true, dune, arrival, _world.AddWork("Heat", []).WorkId);
        _world.AddCollection("Sci-fi (automatic)", manual: false, dune, arrival);

        var preview = await PreviewAsync(dune, arrival);

        Assert.Equal(["Comfort Films", "road trip", "Zebra night"], preview.CuratedCollections.Select(collection => collection.Name));
        var comfort = preview.CuratedCollections[0];
        Assert.Equal(2, comfort.ItemsInRequest);
        Assert.True(comfort.WillBeDeleted);
        var roadTrip = preview.CuratedCollections[1];
        Assert.Equal(1, roadTrip.ItemsInRequest);
        Assert.True(roadTrip.WillBeDeleted);
        var zebra = preview.CuratedCollections[2];
        Assert.Equal(2, zebra.ItemsInRequest);
        Assert.False(zebra.WillBeDeleted);
    }

    [Fact]
    public async Task ACuratedCollectionWithAPersonalMediaSource_IsListed_ButIsNotDeletedWhenItsLastTitleGoes()
    {
        var (dune, _) = _world.AddWork("Dune", []);
        var collection = _world.AddCollection("Dune and holiday photos", manual: true, dune);
        _world.AddViewSource(collection);

        var preview = await PreviewAsync(dune);

        var listed = Assert.Single(preview.CuratedCollections);
        Assert.Equal(collection, listed.CollectionId);
        Assert.Equal(1, listed.ItemsInRequest);
        Assert.False(listed.WillBeDeleted);
    }

    [Theory]
    [InlineData("Playlist")]
    [InlineData("PlaylistFolder")]
    [InlineData("System")]
    public async Task AHandPickedPlaylistOrGrouping_IsListed_ButIsNeverDeletedWithItsLastTitle(string collectionType)
    {
        var (song, _) = _world.AddWork("A song", [], mediaType: "Music", workKind: "child");
        _world.AddCollection("Road trip mix", manual: true, collectionType, song);

        var preview = await PreviewAsync(song);

        var listed = Assert.Single(preview.CuratedCollections);
        Assert.Equal("Road trip mix", listed.Name);
        Assert.Equal(1, listed.ItemsInRequest);
        Assert.False(listed.WillBeDeleted);
    }

    [Fact]
    public async Task ACollectionHoldingOnlyTitlesOutsideTheRequest_IsNotListed()
    {
        var (dune, _) = _world.AddWork("Dune", []);
        var (arrival, _) = _world.AddWork("Arrival", []);
        _world.AddCollection("Only Arrival", manual: true, arrival);

        var preview = await PreviewAsync(dune);

        Assert.Empty(preview.CuratedCollections);
    }

    [Fact]
    public async Task ASeriesLink_IsNotACollectionMembership()
    {
        var (dune, _) = _world.AddWork("Dune", []);
        var series = _world.AddCollection("Dune novels", manual: true);
        using (var connection = _world.Database.CreateConnection())
        {
            connection.Execute("UPDATE works SET collection_id = @series WHERE id = @dune;", new { series, dune });
        }

        var preview = await PreviewAsync(dune);

        Assert.Empty(preview.CuratedCollections);
    }

    // ----- Personal state and processing -----

    [Fact]
    public async Task ProfilesWithProgress_CountsEachPersonOnce_AndIgnoresPersonalActivity()
    {
        var file = _world.WriteFile(_world.Managed, "Dune.mkv");
        var (workId, assetIds) = _world.AddWork("Dune", [file]);
        var assetId = assetIds[0];
        var alex = _world.AddProfile("Alex");
        var bea = _world.AddProfile("Bea");
        var casey = _world.AddProfile("Casey");
        var dana = _world.AddProfile("Dana");
        var now = DateTimeOffset.UtcNow.ToString("O");
        using (var connection = _world.Database.CreateConnection())
        {
            // Alex: two different tables.
            connection.Execute(
                "INSERT INTO user_states (user_id, asset_id, progress_pct) VALUES (@alex, @assetId, 40);",
                new { alex, assetId });
            connection.Execute("""
                INSERT INTO consumption_history (id, profile_id, asset_id, session_key, experience, started_at, updated_at, progress_pct)
                VALUES (@id, @alex, @assetId, 's1', 'watch', @now, @now, 40);
                """, new { id = Guid.NewGuid(), alex, assetId, now });
            // Bea: history, and a reader bookmark that stores her id as text.
            connection.Execute("""
                INSERT INTO consumption_history (id, profile_id, asset_id, session_key, experience, started_at, updated_at, progress_pct)
                VALUES (@id, @bea, @assetId, 's2', 'watch', @now, @now, 10);
                """, new { id = Guid.NewGuid(), bea, assetId, now });
            connection.Execute("""
                INSERT INTO reader_bookmarks (id, user_id, asset_id, chapter_index)
                VALUES (@id, @userId, @assetId, 3);
                """, new { id = Guid.NewGuid(), userId = bea.ToString("D"), assetId });
            // Casey: only a saved item.
            connection.Execute("""
                INSERT INTO profile_saved_items (profile_id, entity_kind, entity_id, saved_at)
                VALUES (@casey, 'Movie', @workId, @now);
                """, new { casey, workId, now });
            // Dana: only the server's own activity record, which is not progress on the title.
            connection.Execute("""
                INSERT INTO personal_activity (id, profile_id, kind, started_at, media_type, title, work_id)
                VALUES (@id, @dana, 'watch', @now, 'Movies', 'Dune', @workId);
                """, new { id = Guid.NewGuid(), dana, now, workId });
        }

        var preview = await PreviewAsync(workId);

        Assert.Equal(3, preview.ProfilesWithProgress);
    }

    [Fact]
    public async Task ProcessingRunning_IsTrueOnlyWhileAnUnfinishedJobTargetsTheTitleOrItsFile()
    {
        var file = _world.WriteFile(_world.Managed, "Dune.mkv");
        var (workId, assetIds) = _world.AddWork("Dune", [file]);

        Assert.False((await PreviewAsync(workId)).ProcessingRunning);

        _world.AddIdentityJob(assetIds[0], "Ready");
        _world.AddIdentityJob(assetIds[0], "QidNeedsReview");
        Assert.False((await PreviewAsync(workId)).ProcessingRunning);

        _world.AddIdentityJob(assetIds[0], "Hydrating");
        Assert.True((await PreviewAsync(workId)).ProcessingRunning);
    }

    [Fact]
    public async Task ProcessingForAnotherTitle_DoesNotCount()
    {
        var (workId, _) = _world.AddWork("Dune", []);
        var (_, otherAssets) = _world.AddWork("Arrival", [_world.WriteFile(_world.Managed, "Arrival.mkv")]);
        _world.AddIdentityJob(otherAssets[0], "Queued");

        Assert.False((await PreviewAsync(workId)).ProcessingRunning);
    }

    // ----- Refusals -----

    [Fact]
    public async Task AViewPersonalItemId_IsNotFound_BecauseViewItemsAreNotTitles()
    {
        var viewItem = _world.AddViewItem();

        var result = await _world.Service.PreviewAsync([viewItem]);

        Assert.False(result.IsSuccess);
        Assert.Equal(LibraryItemDeletionRefusalKind.NotFound, result.Refusal!.Kind);
        Assert.Equal([viewItem], result.Refusal.WorkIds);
    }

    [Fact]
    public async Task AnUnknownIdAlongsideARealOne_IsNotFound_AndNamesOnlyTheUnknownOne()
    {
        var (real, _) = _world.AddWork("Dune", []);
        var unknown = Guid.NewGuid();

        var result = await _world.Service.PreviewAsync([real, unknown]);

        Assert.Equal(LibraryItemDeletionRefusalKind.NotFound, result.Refusal!.Kind);
        Assert.Equal([unknown], result.Refusal.WorkIds);
    }

    [Fact]
    public async Task AParentWork_IsRefused_BecauseOnlyIndividualTitlesCanBeDeleted()
    {
        var (show, _) = _world.AddWork("A TV show", [], mediaType: "TV", workKind: "parent");

        var result = await _world.Service.PreviewAsync([show]);

        Assert.Equal(LibraryItemDeletionRefusalKind.Validation, result.Refusal!.Kind);
        Assert.Equal("Only individual titles can be deleted.", result.Refusal.Message);
    }

    [Fact]
    public async Task ACatalogOnlyWork_IsRefused()
    {
        var (catalog, _) = _world.AddWork("Not owned yet", [], catalogOnly: true);

        var result = await _world.Service.PreviewAsync([catalog]);

        Assert.Equal(LibraryItemDeletionRefusalKind.Validation, result.Refusal!.Kind);
    }

    [Fact]
    public async Task AChildWork_SuchAsAnEpisodeOrASong_IsAccepted()
    {
        var (episode, _) = _world.AddWork("Pilot", [], mediaType: "TV", workKind: "child");

        Assert.True((await _world.Service.PreviewAsync([episode])).IsSuccess);
    }

    [Fact]
    public async Task MoreThan200Titles_AreRefused_AndNoneAtAll_IsRefused()
    {
        var tooMany = Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()).ToList();

        var many = await _world.Service.PreviewAsync(tooMany);
        var none = await _world.Service.PreviewAsync([]);

        Assert.Equal(LibraryItemDeletionRefusalKind.Validation, many.Refusal!.Kind);
        Assert.Equal(LibraryItemDeletionRefusalKind.Validation, none.Refusal!.Kind);
    }

    [Fact]
    public async Task Exactly200DistinctTitles_AreAccepted_AndDuplicatesCollapse()
    {
        var ids = Enumerable.Range(0, 200).Select(index => _world.AddWork($"Title {index}", []).WorkId).ToList();

        var preview = (await _world.Service.PreviewAsync([.. ids, .. ids])).Value!;

        Assert.Equal(200, preview.Items.Count);
        Assert.Equal(ids, preview.Items.Select(item => item.WorkId));
    }

    // ----- Fingerprint -----

    [Fact]
    public async Task TheSameState_GivesTheSameFingerprint_AsLowercaseSha256Hex()
    {
        var media = _world.WriteFile(_world.Managed, Path.Combine("Dune", "Dune.mkv"));
        _world.WriteFile(_world.Managed, Path.Combine("Dune", "Dune.en.srt"));
        var (workId, _) = _world.AddWork("Dune", [media]);
        _world.AddCollection("Comfort Films", manual: true, workId);

        var first = (await PreviewAsync(workId)).Fingerprint;
        var second = (await PreviewAsync(workId)).Fingerprint;

        Assert.Equal(first, second);
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public async Task AddingAFileToTheFolder_ChangesTheFingerprint()
    {
        var media = _world.WriteFile(_world.Managed, Path.Combine("Dune", "Dune.mkv"));
        var (workId, _) = _world.AddWork("Dune", [media]);
        var before = (await PreviewAsync(workId)).Fingerprint;

        _world.WriteFile(_world.Managed, Path.Combine("Dune", "Dune.fr.srt"));

        Assert.NotEqual(before, (await PreviewAsync(workId)).Fingerprint);
    }

    [Fact]
    public async Task ChangingAFilesSize_ChangesTheFingerprint()
    {
        var media = _world.WriteFile(_world.Managed, Path.Combine("Dune", "Dune.mkv"), bytes: 100);
        var (workId, _) = _world.AddWork("Dune", [media]);
        var before = (await PreviewAsync(workId)).Fingerprint;

        File.WriteAllBytes(media, new byte[101]);

        Assert.NotEqual(before, (await PreviewAsync(workId)).Fingerprint);
    }

    [Fact]
    public async Task AddingTheTitleToAHandPickedCollection_ChangesTheFingerprint()
    {
        var (workId, _) = _world.AddWork("Dune", []);
        var collection = _world.AddCollection("Comfort Films", manual: true);
        var before = (await PreviewAsync(workId)).Fingerprint;

        _world.AddMember(collection, workId);

        Assert.NotEqual(before, (await PreviewAsync(workId)).Fingerprint);
    }

    [Fact]
    public async Task AFolderSwitchedToAsIsAfterThePreview_ChangesTheFingerprint_BecauseTheFileNowStays()
    {
        var file = _world.WriteFile(_world.Managed, "Dune.mkv", bytes: 100);
        var (workId, _) = _world.AddWork("Dune", [file]);
        var before = await PreviewAsync(workId);

        _world.SwitchMoviesFolderToAsIs();
        var after = await PreviewAsync(workId);

        Assert.Equal(LibraryItemDeletionOutcomes.Delete, before.Files.Single().Outcome);
        Assert.Equal(LibraryItemDeletionOutcomes.KeepAsIs, after.Files.Single().Outcome);
        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task WhetherProcessingIsRunning_IsNotPartOfTheFingerprint()
    {
        var media = _world.WriteFile(_world.Managed, "Dune.mkv");
        var (workId, assetIds) = _world.AddWork("Dune", [media]);
        var before = await PreviewAsync(workId);

        _world.AddIdentityJob(assetIds[0], "Hydrating");
        var after = await PreviewAsync(workId);

        Assert.False(before.ProcessingRunning);
        Assert.True(after.ProcessingRunning);
        Assert.Equal(before.Fingerprint, after.Fingerprint);
    }

    // ----- The plan the delete action reuses -----

    [Fact]
    public async Task ThePlan_KeepsTheFullPathsAndPoliciesThatTheWireLeavesOut()
    {
        var media = _world.WriteFile(_world.Managed, Path.Combine("Dune", "Dune.mkv"));
        var (workId, _) = _world.AddWork("Dune", [media]);

        var plan = (await _world.Service.BuildPlanAsync([workId])).Value!;

        var file = Assert.Single(plan.Files);
        Assert.Equal(media, file.FullPath);
        Assert.NotNull(file.Policy);
        Assert.True(file.Policy!.AllowDelete);
        Assert.Equal(plan.Preview.Fingerprint, (await _world.Service.PreviewAsync([workId])).Value!.Fingerprint);
    }

    private async Task<LibraryItemDeletionPreview> PreviewAsync(params Guid[] workIds)
    {
        var result = await _world.Service.PreviewAsync(workIds);
        Assert.True(result.IsSuccess, result.Refusal?.Message);
        return result.Value!;
    }
}
