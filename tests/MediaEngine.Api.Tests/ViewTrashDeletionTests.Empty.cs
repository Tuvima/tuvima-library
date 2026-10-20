using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.Activity;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Empty Trash against a real data store and real temp folders: the only action that removes personal files from disk. Every file
/// lives in a temp folder created by the test; nothing outside it is ever named, moved or deleted.
/// </summary>
public sealed partial class ViewTrashDeletionTests
{
    // ---------------------------------------------------------------- the confirmation

    [Fact]
    public async Task EmptyTrash_WithAStaleFingerprint_Answers409WithTheFreshSummary_AndChangesNothing()
    {
        var alice = await CreatePerson();
        var first = await AddPhoto(alice, "first", size: 300);
        _actor.Current = alice.Authority;
        var stale = (await PlanAsync(alice)).Preview.Fingerprint;
        var second = await AddPhoto(alice, "second", size: 500);
        SeedRelatedRows(alice, first.ItemId, "first");

        using var response = await _client.PostAsJsonAsync("/view/trash/empty", new ViewTrashEmptyRequestDto(stale));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ViewTrashEmptyConflictDto>())!;
        Assert.Equal(ViewTrashDeletionService.ChangedMessage, body.Message);
        Assert.Equal(2, body.Preview.DeleteCount);
        Assert.NotEqual(stale, body.Preview.Fingerprint);
        Assert.All(first.Paths.Concat(second.Paths), path => Assert.True(File.Exists(path)));
        Assert.Equal(2, CountItems(first.ItemId, second.ItemId));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM view_gallery_items WHERE item_id = @id;", new { id = first.ItemId }));
        Assert.False(Directory.Exists(StagingRoot(alice)));

        // The fresh fingerprint from the 409 is accepted.
        using var accepted = await _client.PostAsJsonAsync("/view/trash/empty", new ViewTrashEmptyRequestDto(body.Preview.Fingerprint));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(0, CountItems(first.ItemId, second.ItemId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-the-fingerprint")]
    public async Task EmptyTrash_WithAMissingOrWrongFingerprint_DeletesNothing(string? fingerprint)
    {
        var alice = await CreatePerson();
        var photo = await AddPhoto(alice, "keep-me");
        _actor.Current = alice.Authority;

        using var response = await _client.PostAsJsonAsync("/view/trash/empty", new ViewTrashEmptyRequestDto(fingerprint));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(File.Exists(photo.Paths[0]));
        Assert.Equal(1, CountItems(photo.ItemId));
    }

    // ---------------------------------------------------------------- what goes

    [Fact]
    public async Task EmptyTrash_RemovesTheManagedFilesAndThumbnails_AndLeavesEverythingElseAlone()
    {
        var alice = await CreatePerson();
        var gone = await AddPhoto(alice, "going", size: 2048);
        var stays = await AddPhoto(alice, "staying", size: 1024, trash: false);
        var goneThumbnails = WriteThumbnail(gone.ItemId);
        var staysThumbnails = WriteThumbnail(stays.ItemId);

        var (status, result) = await EmptyTrashAsync(alice);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, result!.DeletedCount);
        Assert.Equal(2048, result.BytesFreed);
        Assert.Equal(0, result.SkippedCount);
        Assert.Empty(result.Failed);
        Assert.False(File.Exists(gone.Paths[0]));
        Assert.False(File.Exists(goneThumbnails));
        Assert.False(Directory.Exists(Path.GetDirectoryName(Path.GetDirectoryName(goneThumbnails))));
        Assert.True(File.Exists(stays.Paths[0]));
        Assert.True(File.Exists(staysThumbnails));
        Assert.Equal(0, CountItems(gone.ItemId));
        Assert.Equal(1, CountItems(stays.ItemId));
        Assert.False(Directory.Exists(StagingRoot(alice)));
    }

    [Fact]
    public async Task EmptyTrash_RemovesALivePhotoWithBothOfItsFiles()
    {
        var alice = await CreatePerson();
        var live = await AddItem(alice, LocalAssetMediaKinds.Image, "live",
            Part(alice, "IMG_9.heic", 1000, LocalAssetFileRoles.Primary, alice.Upload),
            Part(alice, "IMG_9.mov", 3000, LocalAssetFileRoles.LivePhotoVideo, alice.Upload));

        var (_, result) = await EmptyTrashAsync(alice);

        Assert.Equal(1, result!.DeletedCount);
        Assert.Equal(4000, result.BytesFreed);
        Assert.All(live.Paths, path => Assert.False(File.Exists(path)));
        Assert.Equal(0, CountItems(live.ItemId));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM local_files;"));
    }

    [Fact]
    public async Task EmptyTrash_OnAnItemWhoseFileIsAlreadyMissing_StillRemovesTheItem()
    {
        var alice = await CreatePerson();
        var photo = await AddPhoto(alice, "vanished");
        File.Delete(photo.Paths[0]);

        var (_, result) = await EmptyTrashAsync(alice);

        Assert.Equal(1, result!.DeletedCount);
        Assert.Equal(0, result.BytesFreed);
        Assert.Equal(0, CountItems(photo.ItemId));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM local_file_sources;"));
    }

    [Fact]
    public async Task EmptyTrash_KeepsAFileAnotherLiveItemStillUses_AndRemovesTheRest()
    {
        var alice = await CreatePerson();
        var video = Part(alice, "clip.mov", 5000, LocalAssetFileRoles.LivePhotoVideo, alice.Upload);
        var live = await AddItem(alice, LocalAssetMediaKinds.Image, "live",
            Part(alice, "clip.heic", 700, LocalAssetFileRoles.Primary, alice.Upload), video);
        var own = await AddItem(alice, LocalAssetMediaKinds.Video, "clip", video with { Role = LocalAssetFileRoles.Primary }, trash: false);

        var (_, result) = await EmptyTrashAsync(alice);

        Assert.Equal(1, result!.DeletedCount);
        Assert.Equal(700, result.BytesFreed);
        Assert.False(File.Exists(live.Paths[0]));
        Assert.True(File.Exists(own.Paths[0]), "the shared video must stay on disk");
        Assert.Equal(0, CountItems(live.ItemId));
        Assert.Equal(1, CountItems(own.ItemId));
        // The staying item still has its file record and its place on disk.
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM local_item_files WHERE item_id = @id;", new { id = own.ItemId }));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM local_file_sources WHERE file_path = @path COLLATE NOCASE;", new { path = own.Paths[0] }));
    }

    [Fact]
    public async Task EmptyTrash_RemovesTwoTrashedItemsThatShareAFileTogether_AndCountsItOnce()
    {
        var alice = await CreatePerson();
        var video = Part(alice, "clip.mov", 5000, LocalAssetFileRoles.LivePhotoVideo, alice.Upload);
        var live = await AddItem(alice, LocalAssetMediaKinds.Image, "live",
            Part(alice, "clip.heic", 700, LocalAssetFileRoles.Primary, alice.Upload), video);
        var own = await AddItem(alice, LocalAssetMediaKinds.Video, "clip", video with { Role = LocalAssetFileRoles.Primary });

        var (_, result) = await EmptyTrashAsync(alice);

        Assert.Equal(2, result!.DeletedCount);
        Assert.Equal(5700, result.BytesFreed);
        Assert.All(live.Paths.Concat(own.Paths), path => Assert.False(File.Exists(path)));
        Assert.Equal(0, CountItems(live.ItemId, own.ItemId));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM local_files;"));
        Assert.False(Directory.Exists(StagingRoot(alice)));
    }

    [Fact]
    public async Task EmptyTrash_LeavesBothItemsAndAllFilesWhenOneOfTwoItemsSharingAFileCannotGo()
    {
        var alice = await CreatePerson();
        var video = Part(alice, "clip.mov", 5000, LocalAssetFileRoles.LivePhotoVideo, alice.Upload);
        var live = await AddItem(alice, LocalAssetMediaKinds.Image, "live",
            Part(alice, "clip.heic", 700, LocalAssetFileRoles.Primary, alice.Upload), video);
        var own = await AddItem(alice, LocalAssetMediaKinds.Video, "clip", video with { Role = LocalAssetFileRoles.Primary });
        var fingerprint = (await PlanAsync(alice)).Preview.Fingerprint;
        // Empty Trash plans first (the gate is asked about three files); after those three calls the video is refused at the re-check.
        using var service = BuildService(new RefuseAfterGate(new MediaEngine.Ingestion.SourceMutationPolicyGate(), 3, ".mov"));

        var outcome = await service.EmptyAsync(alice.Authority, fingerprint);

        Assert.Equal(ViewTrashEmptyStatus.Ok, outcome.Status);
        Assert.Equal(0, outcome.Result!.DeletedCount);
        Assert.Equal(2, outcome.Result.Failed.Count);
        Assert.Contains(outcome.Result.Failed, failure => failure.Reason == ViewTrashDeletionPlanner.NotAllowedReason);
        Assert.Contains(outcome.Result.Failed, failure => failure.Reason == ViewTrashDeletionService.SharedFileHeldReason);
        Assert.All(live.Paths.Concat(own.Paths), path => Assert.True(File.Exists(path)));
        Assert.Equal(2, CountItems(live.ItemId, own.ItemId));
        Assert.False(Directory.Exists(StagingRoot(alice)));
    }

    // ---------------------------------------------------------------- all or nothing

    [WindowsOnlyFact]
    public async Task EmptyTrash_ALivePhotoWhoseVideoIsLocked_KeepsBothFilesInPlaceAndStaysInTrash()
    {
        var alice = await CreatePerson();
        var live = await AddItem(alice, LocalAssetMediaKinds.Image, "live",
            Part(alice, "IMG_5.heic", 1000, LocalAssetFileRoles.Primary, alice.Upload),
            Part(alice, "IMG_5.mov", 3000, LocalAssetFileRoles.LivePhotoVideo, alice.Upload));
        var videoPath = live.Paths.Single(path => path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase));

        ViewTrashEmptyResultDto? result;
        using (new System.IO.FileStream(videoPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            (_, result) = await EmptyTrashAsync(alice);
        }

        Assert.Equal(0, result!.DeletedCount);
        var failure = Assert.Single(result.Failed);
        Assert.Equal(live.ItemId, failure.ItemId);
        Assert.Equal(ViewTrashDeletionService.FileInUseReason, failure.Reason);
        Assert.All(live.Paths, path => Assert.True(File.Exists(path), "both files stay exactly where they were"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM local_items WHERE id = @id AND trashed_at IS NOT NULL;", new { id = live.ItemId }));
        Assert.False(Directory.Exists(StagingRoot(alice)));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM system_activity WHERE action_type = 'PersonalTrashEmptied';"));

        // Once the file is free again the same Trash empties normally.
        var (_, again) = await EmptyTrashAsync(alice);
        Assert.Equal(1, again!.DeletedCount);
        Assert.All(live.Paths, path => Assert.False(File.Exists(path)));
    }

    [Fact]
    public async Task EmptyTrash_WhenTheRecordsCannotBeRemoved_PutsEveryFileBackAndTheItemStaysInTrash()
    {
        var alice = await CreatePerson();
        var live = await AddItem(alice, LocalAssetMediaKinds.Image, "live",
            Part(alice, "IMG_6.heic", 1000, LocalAssetFileRoles.Primary, alice.Upload),
            Part(alice, "IMG_6.mov", 3000, LocalAssetFileRoles.LivePhotoVideo, alice.Upload));
        Execute("CREATE TRIGGER trg_test_block_item_delete BEFORE DELETE ON local_items BEGIN SELECT RAISE(ABORT, 'blocked for the test'); END;");

        var (status, result) = await EmptyTrashAsync(alice);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, result!.DeletedCount);
        var failure = Assert.Single(result.Failed);
        Assert.Equal(ViewTrashDeletionService.RecordsFailedReason, failure.Reason);
        Assert.DoesNotContain("blocked", failure.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.All(live.Paths, path => Assert.True(File.Exists(path)));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM local_items WHERE id = @id AND trashed_at IS NOT NULL;", new { id = live.ItemId }));
        Assert.False(Directory.Exists(StagingRoot(alice)));

        Execute("DROP TRIGGER trg_test_block_item_delete;");
        var (_, retried) = await EmptyTrashAsync(alice);
        Assert.Equal(1, retried!.DeletedCount);
    }

    [Fact]
    public async Task EmptyTrash_AFileThatChangedSizeAfterThePlan_IsNotDeleted_AndItsItemStaysInTrash()
    {
        var alice = await CreatePerson();
        var photo = await AddPhoto(alice, "edited-meanwhile", size: 400);
        var fingerprint = (await PlanAsync(alice)).Preview.Fingerprint;
        // This service's gate is asked once while it plans again and once more for the re-check of the file (the second call).
        using var service = BuildService(new ActionOnCallGate(new MediaEngine.Ingestion.SourceMutationPolicyGate(), 2,
            () => File.AppendAllText(photo.Paths[0], "more")));

        var outcome = await service.EmptyAsync(alice.Authority, fingerprint);

        Assert.Equal(ViewTrashEmptyStatus.Ok, outcome.Status);
        var failure = Assert.Single(outcome.Result!.Failed);
        Assert.Equal(ViewTrashDeletionService.FileChangedReason, failure.Reason);
        Assert.Equal(0, outcome.Result.DeletedCount);
        Assert.True(File.Exists(photo.Paths[0]));
        Assert.Equal(1, CountItems(photo.ItemId));
        Assert.False(Directory.Exists(StagingRoot(alice)));
    }

    [Fact]
    public async Task EmptyTrash_AFileTheSafetyGateRefusesAtTheMomentOfActing_IsLeftAlone()
    {
        var alice = await CreatePerson();
        var photo = await AddPhoto(alice, "refused-late");
        var fingerprint = (await PlanAsync(alice)).Preview.Fingerprint;
        using var service = BuildService(new FlipOnCallGate(new MediaEngine.Ingestion.SourceMutationPolicyGate(), refuseFrom: 2));

        var outcome = await service.EmptyAsync(alice.Authority, fingerprint);

        var failure = Assert.Single(outcome.Result!.Failed);
        Assert.Equal(ViewTrashDeletionPlanner.NotAllowedReason, failure.Reason);
        Assert.True(File.Exists(photo.Paths[0]));
        Assert.Equal(1, CountItems(photo.ItemId));
    }

    // ---------------------------------------------------------------- nothing left behind in the data store

    [Fact]
    public async Task EmptyTrash_LeavesNoRowForTheRemovedItemInAnyItemTable_AndKeepsTheRowsOfTheOneThatStays()
    {
        var alice = await CreatePerson();
        var gone = await AddPhoto(alice, "gone-everywhere");
        var stays = await AddPhoto(alice, "stays-everywhere", trash: false);
        SeedRelatedRows(alice, gone.ItemId, "gone-everywhere");
        SeedRelatedRows(alice, stays.ItemId, "stays-everywhere");
        var tables = ItemColumns();
        Assert.True(tables.Count >= 12, "the walk must cover every table that points at an item; found " + tables.Count);
        foreach (var (table, column) in tables.Where(entry => SeededTables.Contains(entry.Table)))
        {
            Assert.True(Scalar($"SELECT COUNT(*) FROM \"{table}\" WHERE \"{column}\" = @id;", new { id = gone.ItemId }) > 0,
                $"the test should have seeded {table}.{column} for the item before emptying");
        }

        var searchRowsBefore = Scalar("SELECT COUNT(*) FROM local_item_search;");
        var (_, result) = await EmptyTrashAsync(alice);

        Assert.Equal(1, result!.DeletedCount);
        foreach (var (table, column) in tables)
        {
            Assert.True(Scalar($"SELECT COUNT(*) FROM \"{table}\" WHERE \"{column}\" = @id;", new { id = gone.ItemId }) == 0,
                $"{table}.{column} still names the removed item");
        }

        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM local_items WHERE id = @id;", new { id = gone.ItemId }));
        Assert.Equal(searchRowsBefore - 1, Scalar("SELECT COUNT(*) FROM local_item_search;"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM local_item_search WHERE local_item_search MATCH 'gone';"));
        // Rows of the item that stays are untouched.
        foreach (var (table, column) in tables.Where(entry => SeededTables.Contains(entry.Table)))
        {
            Assert.True(Scalar($"SELECT COUNT(*) FROM \"{table}\" WHERE \"{column}\" = @id;", new { id = stays.ItemId }) > 0,
                $"{table}.{column} lost a row of the item that stays");
        }
    }

    [Fact]
    public async Task EmptyTrash_LeavesAGalleryThatBecameEmptyInPlace_AndAGalleryWhoseCoverWasRemovedFallsBack()
    {
        var alice = await CreatePerson();
        var gone = await AddPhoto(alice, "only-photo");
        var kept = await AddPhoto(alice, "kept-photo", trash: false);
        var emptied = InsertGallery(alice, "Beach 2025", gone.ItemId, coverItemId: gone.ItemId);
        var mixed = InsertGallery(alice, "Mixed", gone.ItemId, coverItemId: gone.ItemId);
        AddToGallery(mixed, kept.ItemId);

        await EmptyTrashAsync(alice);

        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM view_galleries WHERE id = @id;", new { id = emptied }));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM view_gallery_items WHERE gallery_id = @id;", new { id = emptied }));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM view_galleries WHERE id = @id AND cover_item_id IS NULL;", new { id = emptied }));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM view_galleries WHERE id = @id AND cover_item_id IS NULL;", new { id = mixed }));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM view_gallery_items WHERE gallery_id = @id AND item_id = @item;", new { id = mixed, item = kept.ItemId }));
    }

    // ---------------------------------------------------------------- the activity entry

    [Fact]
    public async Task EmptyTrash_WritesOneActivityEntryWithCountsAndNoNamesOrTitles()
    {
        var alice = await CreatePerson();
        await AddPhoto(alice, "birthday-secret", size: 1536);
        await AddPhoto(alice, "grandma-secret", size: 512);
        var skipped = await AddPhoto(alice, "pending-secret", size: 100);
        InsertContribution(alice, skipped.ItemId, "pending", "waiting");

        var (_, result) = await EmptyTrashAsync(alice);

        Assert.Equal(2, result!.DeletedCount);
        Assert.Equal(1, result.SkippedCount);
        using var connection = _database.CreateConnection();
        var row = connection.QuerySingle<(string Detail, string Changes, string? Title, byte[]? EntityId)>(
            "SELECT detail, changes_json, collection_name, entity_id FROM system_activity WHERE action_type = 'PersonalTrashEmptied';");
        Assert.Equal("2 items, 2 KB freed, 1 skipped", row.Detail);
        using var changes = JsonDocument.Parse(row.Changes);
        Assert.Equal(2, changes.RootElement.GetProperty("items").GetInt32());
        Assert.Equal(2048, changes.RootElement.GetProperty("bytes_freed").GetInt64());
        Assert.Equal(1, changes.RootElement.GetProperty("skipped").GetInt32());
        Assert.Null(row.Title);
        Assert.Null(row.EntityId);
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM system_activity WHERE action_type = 'PersonalTrashEmptied' AND profile_id = @id;", new { id = alice.ProfileId }));
        var everything = string.Join(' ', connection.Query<string>(
            "SELECT COALESCE(detail, '') || ' ' || COALESCE(changes_json, '') || ' ' || COALESCE(collection_name, '') || ' ' || COALESCE(entity_type, '') FROM system_activity WHERE action_type = 'PersonalTrashEmptied';"));
        Assert.DoesNotContain("secret", everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".jpg", everything, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM system_activity WHERE action_type = 'PersonalTrashEmptied';"));
    }

    [Fact]
    public void TheActivityLog_ShowsEmptiedTrash_PlainlyInTheSystemCategory()
    {
        var entry = new SystemActivityEntry
        {
            ActionType = SystemActionType.PersonalTrashEmptied,
            OccurredAt = DateTimeOffset.UtcNow,
            Detail = "128 items, 4.2 GB freed, 2 skipped",
            ChangesJson = """{"items":128,"bytes_freed":4509715661,"skipped":2}""",
        };

        var dto = ActivityLogPresenter.Present(entry)!;

        Assert.Equal("Emptied Trash", dto.Title);
        Assert.Equal("128 items, 4.2 GB freed, 2 skipped", dto.Detail);
        Assert.Equal(ActivityLogCategoryValues.System, dto.Category);
        Assert.Contains(SystemActionType.PersonalTrashEmptied, ActivityLogCategories.ActionTypesFor(ActivityLogCategoryValues.System));
    }

    // ---------------------------------------------------------------- Shared Library work

    [Fact]
    public async Task EmptyTrash_SkipsAnItemWithPendingSharedLibraryWork_AndKeepsAllOfItsRows()
    {
        var alice = await CreatePerson();
        var waiting = await AddPhoto(alice, "waiting", size: 111);
        var contribution = InsertContribution(alice, waiting.ItemId, "pending", "waiting");
        var going = await AddPhoto(alice, "going", size: 222);

        var (_, result) = await EmptyTrashAsync(alice);

        Assert.Equal(1, result!.DeletedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.True(File.Exists(waiting.Paths[0]));
        Assert.False(File.Exists(going.Paths[0]));
        Assert.Equal(1, CountItems(waiting.ItemId));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM view_shared_contribution_items WHERE item_id = @id AND contribution_id = @contribution;",
            new { id = waiting.ItemId, contribution }));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM local_items WHERE id = @id AND trashed_at IS NOT NULL;", new { id = waiting.ItemId }));
    }

    [Fact]
    public async Task EmptyTrash_AFinishedContributionKeepsItsHistoryRow_WithNoItemLink()
    {
        var alice = await CreatePerson();
        var photo = await AddPhoto(alice, "contributed-earlier");
        var contribution = InsertContribution(alice, photo.ItemId, "accepted", "completed");
        InsertTransfer(photo.ItemId, "completed");
        var linkedRow = Scalar("SELECT COUNT(*) FROM view_shared_contribution_items WHERE contribution_id = @contribution;", new { contribution });

        var (_, result) = await EmptyTrashAsync(alice);

        Assert.Equal(1, result!.DeletedCount);
        Assert.Equal(0, CountItems(photo.ItemId));
        Assert.Equal(linkedRow, Scalar("SELECT COUNT(*) FROM view_shared_contribution_items WHERE contribution_id = @contribution AND item_id IS NULL AND execution_state = 'completed';", new { contribution }));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM view_shared_transfers WHERE item_id IS NULL AND state = 'completed';"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM view_shared_contributions WHERE id = @contribution AND status = 'accepted';", new { contribution }));
    }

    // ---------------------------------------------------------------- who may

    [Fact]
    public async Task EmptyTrash_IsRefusedForAPairedAppAndAServiceApplication_AndDeletesNothing()
    {
        var alice = await CreatePerson();
        var photo = await AddPhoto(alice, "private");
        var fingerprint = (await PlanAsync(alice)).Preview.Fingerprint;
        var pairedApp = new RequestAuthority(
            PrincipalKind.DelegatedUserClient, true, AccountId: alice.Authority.AccountId, ActiveProfileId: alice.ProfileId,
            ApplicationId: Guid.NewGuid(), AccountEnabled: true, GrantEnabled: true, ApplicationEnabled: true);
        var service = new RequestAuthority(
            PrincipalKind.ServiceApplication, true, ApplicationId: Guid.NewGuid(), ApplicationEnabled: true,
            ApplicationIsAdministrator: true);

        foreach (var caller in new[] { pairedApp, service })
        {
            _actor.Current = caller;
            using var response = await _client.PostAsJsonAsync("/view/trash/empty", new ViewTrashEmptyRequestDto(fingerprint));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        _actor.Current = new RequestAuthority(PrincipalKind.Anonymous, false);
        using var anonymous = await _client.PostAsJsonAsync("/view/trash/empty", new ViewTrashEmptyRequestDto(fingerprint));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.True(File.Exists(photo.Paths[0]));
        Assert.Equal(1, CountItems(photo.ItemId));
    }

    [Fact]
    public async Task EmptyTrashItself_RefusesAPairedApp()
    {
        var alice = await CreatePerson();
        var photo = await AddPhoto(alice, "private");
        var pairedApp = new RequestAuthority(
            PrincipalKind.DelegatedUserClient, true, AccountId: alice.Authority.AccountId, ActiveProfileId: alice.ProfileId,
            ApplicationId: Guid.NewGuid(), AccountEnabled: true, GrantEnabled: true, ApplicationEnabled: true);
        using var scope = _app.Services.CreateScope();

        var outcome = await scope.ServiceProvider.GetRequiredService<ViewTrashDeletionService>()
            .EmptyAsync(pairedApp, (await PlanAsync(alice)).Preview.Fingerprint);

        Assert.Equal(ViewTrashEmptyStatus.Forbidden, outcome.Status);
        Assert.True(File.Exists(photo.Paths[0]));
    }

    [Fact]
    public async Task EmptyTrash_NeverTouchesAnotherPersonsTrash()
    {
        var household = Guid.NewGuid();
        var alice = await CreatePerson(household: household);
        var bob = await CreatePerson(household: household, householdAdmin: true, serverAdmin: true);
        var mine = await AddPhoto(alice, "mine");
        var theirs = await AddPhoto(bob, "theirs", size: 999);
        SeedRelatedRows(bob, theirs.ItemId, "theirs");

        var (_, result) = await EmptyTrashAsync(alice);

        Assert.Equal(1, result!.DeletedCount);
        Assert.False(File.Exists(mine.Paths[0]));
        Assert.True(File.Exists(theirs.Paths[0]));
        Assert.Equal(1, CountItems(theirs.ItemId));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM view_gallery_items WHERE item_id = @id;", new { id = theirs.ItemId }));

        // Even an administrator of the household empties only their own.
        var (_, theirResult) = await EmptyTrashAsync(bob);
        Assert.Equal(1, theirResult!.DeletedCount);
        Assert.False(File.Exists(theirs.Paths[0]));
    }

    [Fact]
    public async Task AProfileWithoutDocuments_EmptyTrashLeavesItsDocumentsOnDisk()
    {
        var alice = await CreatePerson();
        var photo = await AddPhoto(alice, "pic");
        var lease = await AddItem(alice, LocalAssetMediaKinds.Document, "lease", Part(alice, "lease.pdf", 800, LocalAssetFileRoles.Primary, alice.Upload));
        await _policies.SavePolicyAsync(ViewProfilePolicy.Default(alice.ProfileId) with { DocumentsEnabled = false });
        var fingerprint = (await PlanAsync(alice)).Preview.Fingerprint;
        _actor.Current = alice.Authority;

        using var response = await _client.PostAsJsonAsync("/view/trash/empty", new ViewTrashEmptyRequestDto(fingerprint));
        var result = await response.Content.ReadFromJsonAsync<ViewTrashEmptyResultDto>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, result!.DeletedCount);
        Assert.False(File.Exists(photo.Paths[0]));
        Assert.True(File.Exists(lease.Paths[0]));
        Assert.Equal(1, CountItems(lease.ItemId));
    }

    [Fact]
    public async Task EmptyTrash_NeverDeletesAFileInALinkedFolder()
    {
        var alice = await CreatePerson();
        var linked = await AddLinkedSource(alice);
        var folder = ExternalPath(linked);
        var readOnly = await AddPhoto(alice, "on-the-nas", source: linked, folder: folder);
        var managed = await AddPhoto(alice, "uploaded");

        var (_, result) = await EmptyTrashAsync(alice);

        Assert.Equal(1, result!.DeletedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.True(File.Exists(readOnly.Paths[0]));
        Assert.False(File.Exists(managed.Paths[0]));
        Assert.Equal(1, CountItems(readOnly.ItemId));
    }

    [Fact]
    public async Task EmptyTrash_NeverFollowsALinkOutOfTheProfileFolder()
    {
        var alice = await CreatePerson();
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside-" + Guid.NewGuid().ToString("N"))).FullName;
        var victim = Path.Combine(outside, "precious.jpg");
        await File.WriteAllBytesAsync(victim, new byte[64]);
        var link = Path.Combine(alice.UploadFolder, "escape");
        Directory.CreateDirectory(alice.UploadFolder);
        if (!TestLinks.TryCreateDirectoryLink(link, outside))
        {
            // This account cannot make a link here; the containment checks for links are also covered by the planner tests.
            return;
        }

        _links.Add(link);
        await AddPhoto(alice, "precious", folder: link, realFolder: outside);

        var (_, result) = await EmptyTrashAsync(alice);

        Assert.Equal(0, result!.DeletedCount);
        Assert.True(File.Exists(victim), "a file reached through a link out of the profile folder must never be deleted");
    }

    // ---------------------------------------------------------------- the scanner and the staging folder

    [Fact]
    public async Task TheScanner_DoesNotBringBackARemovedItem_AndIgnoresTheStagingFolder()
    {
        var alice = await CreatePerson();
        var removed = await AddPhoto(alice, "removed", size: 128);
        await EmptyTrashAsync(alice);
        var indexer = new ViewLibraryService(_assets, _configuration, _spaces, _storage, NullLogger<ViewLibraryService>.Instance);

        // A real picture goes in beside a copy parked in the profile's staging folder; only the first can be added.
        Directory.CreateDirectory(alice.UploadFolder);
        WritePng(Path.Combine(alice.UploadFolder, "fresh.png"));
        var staging = Directory.CreateDirectory(Path.Combine(StagingRoot(alice), Guid.NewGuid().ToString("D"))).FullName;
        var parked = Path.Combine(staging, "0000_parked.png");
        WritePng(parked);

        var scan = await indexer.ScanAsync(alice.Space.LibraryId);

        Assert.Equal(1, scan!.ItemsAdded);
        Assert.Equal(0, CountItems(removed.ItemId));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM local_file_sources WHERE file_path LIKE '%parked.png';"));
        await Assert.ThrowsAsync<InvalidDataException>(() => indexer.IndexPathAsync(alice.Space.LibraryId, parked));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM local_file_sources WHERE file_path LIKE '%parked.png';"));
    }

    [Theory]
    [InlineData("C:/Library/View/Profiles/abc/.tuvima-staging/1234/0000_a.jpg", true)]
    [InlineData("C:\\Library\\View\\Profiles\\abc\\.tuvima-staging", true)]
    [InlineData("/library/view/profiles/abc/.TUVIMA-STAGING/1234/a.jpg", true)]
    [InlineData("/home/me/.tuvima-staging/1234/a.jpg", false)]
    [InlineData("/library/view/Profiles/abc/Timeline/.tuvima-staging/a.jpg", false)]
    [InlineData("/library/view/Profiles/.tuvima-staging/a.jpg", false)]
    [InlineData("/library/view/profiles/abc/Timeline/a.jpg", false)]
    [InlineData("/library/view/profiles/abc/Timeline/not.tuvima-staging.jpg", false)]
    [InlineData("", false)]
    public void TheStagingFolder_IsRecognisedByAnyStepOfAPath(string path, bool expected)
    {
        Assert.Equal(expected, ViewStorageService.IsInsideStagingFolder(path));
    }

    // ---------------------------------------------------------------- the recovery sweep

    [Fact]
    public async Task TheRecoverySweep_PutsFilesBack_WhenTheirItemWasNeverRemoved()
    {
        var alice = await CreatePerson();
        var photo = await AddPhoto(alice, "crashed-midway");
        var staged = ParkLikeACrash(alice, photo.ItemId, photo.Paths[0]);
        Assert.False(File.Exists(photo.Paths[0]));
        Assert.True(File.Exists(staged));

        var report = await Recovery().SweepAsync();

        Assert.Equal(1, report.ItemsRestored);
        Assert.True(File.Exists(photo.Paths[0]), "the file is back exactly where it was");
        Assert.False(File.Exists(staged));
        Assert.False(Directory.Exists(StagingRoot(alice)));
        Assert.Equal(1, CountItems(photo.ItemId));
    }

    [Fact]
    public async Task TheRecoverySweep_DeletesParkedFiles_WhenTheirItemIsAlreadyGone()
    {
        var alice = await CreatePerson();
        var photo = await AddPhoto(alice, "removed-then-crashed");
        var staged = ParkLikeACrash(alice, photo.ItemId, photo.Paths[0]);
        Execute("DELETE FROM local_items WHERE id = @id;", new { id = photo.ItemId });

        var report = await Recovery().SweepAsync();

        Assert.Equal(1, report.ItemsFinished);
        Assert.False(File.Exists(staged));
        Assert.False(File.Exists(photo.Paths[0]));
        Assert.False(Directory.Exists(StagingRoot(alice)));
    }

    [Fact]
    public async Task TheRecoverySweep_NeverTrustsAManifestThatPointsOutsideTheProfileFolder()
    {
        var alice = await CreatePerson();
        var photo = await AddPhoto(alice, "tampered");
        var outsideFolder = Directory.CreateDirectory(Path.Combine(_root, "elsewhere-" + Guid.NewGuid().ToString("N"))).FullName;
        var outsidePath = Path.Combine(outsideFolder, "restored-here.jpg");
        var folder = Path.Combine(StagingRoot(alice), photo.ItemId.ToString("D"));
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "0000_a.jpg"), new byte[8]);
        var victim = Path.Combine(outsideFolder, "victim.jpg");
        await File.WriteAllBytesAsync(victim, new byte[8]);
        WriteManifest(folder, photo.ItemId, (outsidePath, "0000_a.jpg"), (photo.Paths[0], "../../../../elsewhere"));

        // The item exists: the parked file is NOT written to the outside original, and the traversal name is never followed.
        await Recovery().SweepAsync();

        Assert.False(File.Exists(outsidePath));
        Assert.True(File.Exists(Path.Combine(folder, "0000_a.jpg")), "left in staging, not restored outside the profile folder");
        Assert.True(File.Exists(victim));

        // The item is gone: only plain names inside the item's own staging folder are deleted; the traversal name is ignored.
        Execute("DELETE FROM local_items WHERE id = @id;", new { id = photo.ItemId });
        await Recovery().SweepAsync();

        Assert.True(File.Exists(victim));
        Assert.False(File.Exists(Path.Combine(folder, "0000_a.jpg")));
    }

    [Fact]
    public async Task TheRecoverySweep_LeavesAFolderWithoutAReadableManifestAlone()
    {
        var alice = await CreatePerson();
        var folder = Path.Combine(StagingRoot(alice), Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(folder);
        var orphan = Path.Combine(folder, "0000_orphan.jpg");
        await File.WriteAllBytesAsync(orphan, new byte[4]);
        await File.WriteAllTextAsync(Path.Combine(folder, "manifest.json"), "{ not json");
        var notAnItem = Directory.CreateDirectory(Path.Combine(StagingRoot(alice), "not-a-guid")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(notAnItem, "keep.jpg"), new byte[4]);

        var report = await Recovery().SweepAsync();

        Assert.Equal(2, report.FoldersLeftAlone);
        Assert.True(File.Exists(orphan));
        Assert.True(File.Exists(Path.Combine(folder, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(notAnItem, "keep.jpg")));
    }

    [Fact]
    public async Task TheRecoverySweep_RemovesAnEmptyStagingFolderLeftByACrashBeforeAnythingMoved()
    {
        var alice = await CreatePerson();
        var folder = Path.Combine(StagingRoot(alice), Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(folder);

        var report = await Recovery().SweepAsync();

        Assert.Equal(1, report.ItemsFinished);
        Assert.False(Directory.Exists(folder));
    }

    // ---------------------------------------------------------------- helpers for Empty Trash

    private sealed class WindowsOnlyFactAttribute : FactAttribute
    {
        public WindowsOnlyFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "A file open without sharing stops it being renamed only on Windows; other systems let a rename through, so there is nothing to refuse.";
            }
        }
    }

    private async Task<(HttpStatusCode Status, ViewTrashEmptyResultDto? Result)> EmptyTrashAsync(Person person, string? fingerprint = null)
    {
        fingerprint ??= (await PlanAsync(person)).Preview.Fingerprint;
        _actor.Current = person.Authority;
        using var response = await _client.PostAsJsonAsync("/view/trash/empty", new ViewTrashEmptyRequestDto(fingerprint));
        var result = response.StatusCode == HttpStatusCode.OK
            ? await response.Content.ReadFromJsonAsync<ViewTrashEmptyResultDto>()
            : null;
        return (response.StatusCode, result);
    }

    /// <summary>A service built by hand around a given gate, planning with the same gate.</summary>
    private ScopedService BuildService(ISourceMutationPolicyGate gate, IViewTrashFileMover? mover = null)
    {
        var scope = _app.Services.CreateScope();
        var planner = new ViewTrashDeletionPlanner(
            new ViewTrashRepository(_database), _spaces, _policies, _storage, gate,
            scope.ServiceProvider.GetRequiredService<IViewResourceAuthorizationService>(),
            NullLogger<ViewTrashDeletionPlanner>.Instance);
        var purge = new ViewTrashPurgeRepository(_database);
        var recovery = new ViewTrashRecoveryService(_spaces, _storage, purge, NullLogger<ViewTrashRecoveryService>.Instance);
        return new ScopedService(
            new ViewTrashDeletionService(planner, _spaces, _storage, gate, purge, recovery, _assetPaths,
                new SystemActivityRepository(_database), NullLogger<ViewTrashDeletionService>.Instance, mover),
            scope);
    }

    private sealed class ScopedService(ViewTrashDeletionService service, IServiceScope scope) : IDisposable
    {
        public Task<ViewTrashEmptyOutcome> EmptyAsync(RequestAuthority authority, string? fingerprint) =>
            service.EmptyAsync(authority, fingerprint);

        public void Dispose() => scope.Dispose();
    }

    private ViewTrashRecoveryService Recovery() => new(
        _spaces, _storage, new ViewTrashPurgeRepository(_database), NullLogger<ViewTrashRecoveryService>.Instance);

    /// <summary>Allows every call, then refuses any path ending in a given suffix once a number of calls were made.</summary>
    private sealed class RefuseAfterGate(ISourceMutationPolicyGate inner, int afterCalls, string suffix) : ISourceMutationPolicyGate
    {
        private int _calls;

        public SourceMutationDecision Evaluate(SourceMutationRequest request) =>
            Interlocked.Increment(ref _calls) > afterCalls && request.Path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? new SourceMutationDecision(false, "refused by the test")
                : inner.Evaluate(request);
    }

    /// <summary>Allows every call until the given one, then refuses.</summary>
    private sealed class FlipOnCallGate(ISourceMutationPolicyGate inner, int refuseFrom) : ISourceMutationPolicyGate
    {
        private int _calls;

        public SourceMutationDecision Evaluate(SourceMutationRequest request) =>
            Interlocked.Increment(ref _calls) >= refuseFrom
                ? new SourceMutationDecision(false, "refused by the test")
                : inner.Evaluate(request);
    }

    /// <summary>Allows everything, but runs an action on one numbered call (to change the world between the plan and the act).</summary>
    private sealed class ActionOnCallGate(ISourceMutationPolicyGate inner, int onCall, Action action) : ISourceMutationPolicyGate
    {
        private int _calls;

        public SourceMutationDecision Evaluate(SourceMutationRequest request)
        {
            if (Interlocked.Increment(ref _calls) == onCall)
            {
                action();
            }

            return inner.Evaluate(request);
        }
    }

    private string StagingRoot(Person person) => Path.Combine(_storage.GetProfileRoot(person.Space), ViewStorageService.StagingFolderName);

    private static string ExternalPath(ViewSource source) => source.ExternalPath!;

    private string WriteThumbnail(Guid itemId)
    {
        var path = _assetPaths.GetCentralDerivedPath("local-item", itemId, "thumbnail", "thumbnail-640.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[16]);
        return path;
    }

    private static void WritePng(string path)
    {
        using var bitmap = new SKBitmap(4, 4);
        bitmap.Erase(SKColors.Teal);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data.ToArray());
    }

    /// <summary>Parks one file the way Empty Trash does (manifest first, then a rename), as if the Engine stopped right after.</summary>
    private string ParkLikeACrash(Person person, Guid itemId, string original)
    {
        var folder = Path.Combine(StagingRoot(person), itemId.ToString("D"));
        Directory.CreateDirectory(folder);
        var stagedName = "0000_" + Path.GetFileName(original);
        WriteManifest(folder, itemId, (original, stagedName));
        var staged = Path.Combine(folder, stagedName);
        File.Move(original, staged);
        return staged;
    }

    private static void WriteManifest(string folder, Guid itemId, params (string Original, string Staged)[] files) =>
        File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(new
        {
            ItemId = itemId,
            Files = files.Select(file => new { file.Original, file.Staged }).ToArray(),
        }));

    private long CountItems(params Guid[] ids)
    {
        using var connection = _database.CreateConnection();
        return ids.Sum(id => connection.ExecuteScalar<long>("SELECT COUNT(*) FROM local_items WHERE id = @id;", new { id }));
    }

    private long Scalar(string sql, object? parameters = null)
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<long>(sql, parameters);
    }

    private void Execute(string sql, object? parameters = null)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(sql, parameters);
    }

    /// <summary>Every table and column that names a local item (item_id, cover_item_id, origin_item_id, ...), read from the data structure itself.</summary>
    private List<(string Table, string Column)> ItemColumns()
    {
        using var connection = _database.CreateConnection();
        var result = new List<(string, string)>();
        foreach (var table in connection.Query<string>(
                     "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' ORDER BY name;"))
        {
            foreach (var column in connection.Query<string>($"SELECT name FROM pragma_table_info('{table}');"))
            {
                if (column == "item_id" || column.EndsWith("_item_id", StringComparison.Ordinal))
                {
                    // contribution_item_id names a contribution row, not a local item.
                    if (column != "contribution_item_id")
                    {
                        result.Add((table, column));
                    }
                }
            }
        }

        return result;
    }

    private static readonly HashSet<string> SeededTables = new(StringComparer.Ordinal)
    {
        "local_item_files", "local_item_metadata", "local_item_tag_links", "local_item_annotations", "local_item_search_keys",
        "doc_item_details", "doc_field_values", "doc_item_field_sources", "view_gallery_items", "device_photo_downloads",
    };

    /// <summary>Gives an item a row in every kind of table that points at it, so a removal that missed one would show.</summary>
    private void SeedRelatedRows(Person person, Guid itemId, string title)
    {
        var now = DateTimeOffset.UtcNow;
        var tagId = Guid.NewGuid();
        var fieldId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT OR IGNORE INTO local_item_metadata (item_id, updated_at) VALUES (@itemId, @now);
                INSERT INTO tags (id, name, created_at) VALUES (@tagId, @tagName, @now);
                INSERT INTO local_item_tag_links (item_id, tag_id, added_at) VALUES (@itemId, @tagId, @now);
                INSERT INTO local_item_annotations (id, item_id, annotation_kind, annotation_value, source, created_at)
                    VALUES (@annotationId, @itemId, 'person', 'Someone', 'test', @now);
                INSERT INTO doc_item_details (item_id, updated_at) VALUES (@itemId, @now);
                INSERT INTO doc_field_definitions (id, name, kind, created_at) VALUES (@fieldId, @fieldName, 'text', @now);
                INSERT INTO doc_field_values (item_id, field_id, text_value) VALUES (@itemId, @fieldId, 'a value');
                INSERT INTO doc_item_field_sources (item_id, slot, field_id, set_by) VALUES (@itemId, 'field', @fieldId, 'person');
                """, new
            {
                itemId,
                now,
                tagId,
                tagName = "tag-" + tagId.ToString("N"),
                annotationId = Guid.NewGuid(),
                fieldId,
                fieldName = "field-" + fieldId.ToString("N"),
            });
        }

        InsertGallery(person, "Gallery for " + title, itemId, coverItemId: null);

        // A paired phone's offline copy. The device and profile rows are not needed to prove the cascade, so the row is written with
        // foreign keys off on a connection of its own; the cascade on the item is what is being tested.
        using var raw = new SqliteConnection($"Data Source={Path.Combine(_root, "library.db")}");
        raw.Open();
        raw.Execute("PRAGMA foreign_keys = OFF;");
        raw.Execute("""
            INSERT INTO device_photo_downloads (device_id, profile_id, item_id, stored_bytes, stored_at, updated_at)
            VALUES (@device, @profile, @itemId, 10, @now, @now);
            """, new { device = Guid.NewGuid(), profile = person.ProfileId, itemId, now });
        raw.Execute("PRAGMA foreign_keys = ON;");
    }

    private Guid InsertGallery(Person person, string name, Guid itemId, Guid? coverItemId)
    {
        var galleryId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        Execute("""
            INSERT INTO view_galleries (id, owner_profile_id, personal_space_id, name, gallery_kind, cover_item_id, created_at, updated_at)
            VALUES (@galleryId, @profile, @space, @name, 'manual', @coverItemId, @now, @now);
            """, new { galleryId, profile = person.ProfileId, space = person.Space.Id, name, coverItemId, now });
        AddToGallery(galleryId, itemId);
        return galleryId;
    }

    private void AddToGallery(Guid galleryId, Guid itemId) =>
        Execute("INSERT INTO view_gallery_items (gallery_id, item_id, position, added_at) VALUES (@galleryId, @itemId, 0, @now);",
            new { galleryId, itemId, now = DateTimeOffset.UtcNow });

    // ---------------------------------------------------------------- review fixes

    [Fact]
    public async Task EmptyTrash_AnotherItemThatLinksTheSameFileAfterThePlan_StopsTheRemoval_AndEveryFileGoesBack()
    {
        var alice = await CreatePerson();
        var live = await AddItem(alice, LocalAssetMediaKinds.Image, "live",
            Part(alice, "IMG_7.heic", 1000, LocalAssetFileRoles.Primary, alice.Upload),
            Part(alice, "IMG_7.mov", 3000, LocalAssetFileRoles.LivePhotoVideo, alice.Upload));
        var fingerprint = (await PlanAsync(alice)).Preview.Fingerprint;
        var newcomer = Guid.NewGuid();
        // This service's gate is asked once per file while it plans (two files, calls 1 and 2) and once per file at the re-check;
        // the other item appears at the first re-check (call 3).
        using var service = BuildService(new ActionOnCallGate(new MediaEngine.Ingestion.SourceMutationPolicyGate(), 3, () =>
        {
            var now = DateTimeOffset.UtcNow;
            Execute("""
                INSERT INTO local_items (id, scope_kind, personal_space_id, owner_profile_id, library_id, media_kind, primary_file_name, primary_mime_type, created_at, updated_at)
                VALUES (@newcomer, 'personal', @space, @profile, @library, 'video', 'IMG_7.mov', 'video/quicktime', @now, @now);
                INSERT INTO local_item_files (item_id, file_id, role, position, added_at)
                SELECT @newcomer, lif.file_id, 'primary', 0, @now FROM local_item_files lif
                 WHERE lif.item_id = @item AND lif.role = 'live_photo_video';
                """, new { newcomer, space = alice.Space.Id, profile = alice.ProfileId, library = alice.Space.LibraryId, item = live.ItemId, now });
        }));

        var outcome = await service.EmptyAsync(alice.Authority, fingerprint);

        var failure = Assert.Single(outcome.Result!.Failed);
        Assert.Equal(live.ItemId, failure.ItemId);
        Assert.Equal(ViewTrashDeletionService.FileChangedReason, failure.Reason);
        Assert.Equal(0, outcome.Result.DeletedCount);
        Assert.All(live.Paths, path => Assert.True(File.Exists(path), "every file is back where it was"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM local_items WHERE id = @id AND trashed_at IS NOT NULL;", new { id = live.ItemId }));
        Assert.Equal(1, CountItems(newcomer));
        Assert.False(Directory.Exists(StagingRoot(alice)));
    }

    [Fact]
    public async Task EmptyTrash_AStagingFolderThatAlreadyExists_IsNeverReusedOrOverwritten_AndItsItemStaysInTrash()
    {
        var alice = await CreatePerson();
        var photo = await AddPhoto(alice, "blocked-by-leftovers");
        var folder = Path.Combine(StagingRoot(alice), photo.ItemId.ToString("D"));
        Directory.CreateDirectory(folder);
        var manifestPath = Path.Combine(folder, "manifest.json");
        var leftover = JsonSerializer.Serialize(new { ItemId = photo.ItemId, Files = Array.Empty<object>() });
        await File.WriteAllTextAsync(manifestPath, leftover);
        await File.WriteAllBytesAsync(Path.Combine(folder, "0000_earlier.jpg"), new byte[3]);

        var (_, result) = await EmptyTrashAsync(alice);

        Assert.Equal(0, result!.DeletedCount);
        var failure = Assert.Single(result.Failed);
        Assert.Equal(ViewTrashDeletionService.NotPreparedReason, failure.Reason);
        Assert.True(File.Exists(photo.Paths[0]));
        Assert.Equal(1, CountItems(photo.ItemId));
        Assert.Equal(leftover, await File.ReadAllTextAsync(manifestPath));
        Assert.True(File.Exists(Path.Combine(folder, "0000_earlier.jpg")));
    }

    [Fact]
    public async Task EmptyTrash_AMoveThatFailsInAnUnexpectedWay_PutsThatItemsFilesBack_AndTheOtherItemsStillGo()
    {
        var alice = await CreatePerson();
        var bad = await AddItem(alice, LocalAssetMediaKinds.Image, "bad",
            Part(alice, "bad.heic", 1000, LocalAssetFileRoles.Primary, alice.Upload),
            Part(alice, "bad.mov", 3000, LocalAssetFileRoles.LivePhotoVideo, alice.Upload));
        var good = await AddPhoto(alice, "good", size: 700);
        var fingerprint = (await PlanAsync(alice)).Preview.Fingerprint;
        using var service = BuildService(new MediaEngine.Ingestion.SourceMutationPolicyGate(),
            new ThrowingMover(path => path.EndsWith("bad.mov", StringComparison.OrdinalIgnoreCase)));

        var outcome = await service.EmptyAsync(alice.Authority, fingerprint);

        Assert.Equal(ViewTrashEmptyStatus.Ok, outcome.Status);
        Assert.Equal(1, outcome.Result!.DeletedCount);
        Assert.Equal(700, outcome.Result.BytesFreed);
        var failure = Assert.Single(outcome.Result.Failed);
        Assert.Equal(bad.ItemId, failure.ItemId);
        Assert.Equal(ViewTrashDeletionService.NotPreparedReason, failure.Reason);
        Assert.DoesNotContain("NotSupported", failure.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.All(bad.Paths, path => Assert.True(File.Exists(path)));
        Assert.False(File.Exists(good.Paths[0]));
        Assert.Equal(1, CountItems(bad.ItemId));
        Assert.Equal(0, CountItems(good.ItemId));
        Assert.False(Directory.Exists(StagingRoot(alice)));
        // The activity entry still records what was removed.
        using var connection = _database.CreateConnection();
        Assert.Equal("1 item, 700 B freed, 1 skipped", connection.QuerySingle<string>(
            "SELECT detail FROM system_activity WHERE action_type = 'PersonalTrashEmptied';"));
    }

    [WindowsOnlyFact]
    public void TheStagingFolder_IsHidden_OnWindows()
    {
        var root = Path.Combine(_root, "hidden-check", "Profiles", "abc");
        var itemFolder = Path.Combine(root, ViewStorageService.StagingFolderName, Guid.NewGuid().ToString("D"));

        ViewTrashStaging.CreateItemFolder(itemFolder, NullLogger.Instance);

        Assert.True((File.GetAttributes(Path.GetDirectoryName(itemFolder)!) & FileAttributes.Hidden) != 0);
    }

    /// <summary>Moves like the real thing, except it throws a surprising exception for the files the test picks.</summary>
    private sealed class ThrowingMover(Func<string, bool> throwFor) : IViewTrashFileMover
    {
        public void Move(string source, string destination)
        {
            if (throwFor(source))
            {
                throw new NotSupportedException("the test made this move fail");
            }

            File.Move(source, destination);
        }
    }
}
