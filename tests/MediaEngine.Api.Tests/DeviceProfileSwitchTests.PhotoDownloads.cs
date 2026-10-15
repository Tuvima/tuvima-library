using System.Security.Claims;
using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Authorization;
using MediaEngine.Storage.Playback;
using Microsoft.AspNetCore.Http;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Photos on the paired app's download list (kind <c>photo</c>): reported, listed (no expiry), counted under the same
/// 500-entry cap as variants, forgotten, kept apart per person, and judged by the photo access rules the routes use.
/// The access rules themselves are tested with the real View stack in <c>ViewClientEndpointTests</c>.
/// </summary>
public sealed partial class DeviceProfileSwitchTests
{
    private DeviceDownloadService PhotoDownloads(SpyPhotoAccess? access) =>
        new(_devices, new DeviceDownloadRepository(_database), TimeProvider.System, access);

    private static UpsertDeviceDownloadRequest KeepPhoto(Guid? itemId, long bytes = 400_000, DateTimeOffset? storedAt = null) => new()
    {
        Kind = DeviceDownloadKinds.Photo,
        PhotoItemId = itemId,
        StoredBytes = bytes,
        StoredAt = storedAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
    };

    private Guid InsertSharedPhoto()
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT OR IGNORE INTO households (id, name, created_at)
            SELECT @household, 'Test household', @now WHERE NOT EXISTS (SELECT 1 FROM households);
            INSERT OR IGNORE INTO view_shared_library (household_id, library_id, created_at, updated_at)
            SELECT id, @library, @now, @now FROM households ORDER BY rowid LIMIT 1;
            INSERT INTO local_items (id, scope_kind, library_id, media_kind, primary_file_name, primary_mime_type, created_at, updated_at)
            VALUES (@id, 'shared', (SELECT library_id FROM view_shared_library LIMIT 1), 'image', 'photo.jpg', 'image/jpeg', @now, @now);
            """,
            new { id, household = Guid.NewGuid(), library = Guid.NewGuid(), now });
        return id;
    }

    [Fact]
    public async Task Photos_AreReportedListedUpdatedAndForgotten_WithNoExpiry()
    {
        var photo = InsertSharedPhoto();
        var access = new SpyPhotoAccess().Allow(photo);
        var service = PhotoDownloads(access);
        var storedAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        Assert.Equal(DeviceDownloadOutcome.Ok, await service.UpsertAsync(PhoneAuthority(_mary.Id), KeepPhoto(photo, 600, storedAt), NoVariants));

        var entry = Assert.Single((await service.ListAsync(PhoneAuthority(_mary.Id))).Downloads!.Downloads);
        Assert.Equal(DeviceDownloadKinds.Photo, entry.Kind);
        Assert.Equal(photo, entry.PhotoItemId);
        Assert.Equal(Guid.Empty, entry.AssetId);
        Assert.Equal(Guid.Empty, entry.VariantId);
        Assert.Equal(600, entry.StoredBytes);
        Assert.Equal(storedAt, entry.StoredAt);
        Assert.Null(entry.ExpiresAt);

        // Reporting the same photo again updates the one entry; a negative size is zero; a future time is the server's now.
        var before = DateTimeOffset.UtcNow;
        Assert.Equal(DeviceDownloadOutcome.Ok, await service.UpsertAsync(
            PhoneAuthority(_mary.Id), KeepPhoto(photo, -9, DateTimeOffset.UtcNow.AddYears(1)), NoVariants));
        var updated = Assert.Single((await service.ListAsync(PhoneAuthority(_mary.Id))).Downloads!.Downloads);
        Assert.Equal(0, updated.StoredBytes);
        Assert.InRange(updated.StoredAt, before, DateTimeOffset.UtcNow);

        Assert.Equal(DeviceDownloadOutcome.Ok, await service.RemovePhotoAsync(PhoneAuthority(_mary.Id), photo));
        Assert.Empty((await service.ListAsync(PhoneAuthority(_mary.Id))).Downloads!.Downloads);
        Assert.Equal(DeviceDownloadOutcome.NotFound, await service.RemovePhotoAsync(PhoneAuthority(_mary.Id), photo));
    }

    [Fact]
    public async Task TheKindDecidesWhichIdIsNeeded_AndBooksAndUnknownKindsAreRefused()
    {
        var photo = InsertSharedPhoto();
        var service = PhotoDownloads(new SpyPhotoAccess().Allow(photo));
        var phone = PhoneAuthority(_mary.Id);
        var film = await InsertOwnedMovieAsync("Family film", "PG");
        var variant = InsertVariant(film.AssetId, _mary.Id, _phoneId);

        // Reserved and unknown kinds are a bad request, whatever else the body holds.
        foreach (var kind in new[] { DeviceDownloadKinds.Book, "movie", "photos" })
        {
            Assert.Equal(DeviceDownloadOutcome.BadRequest, await service.UpsertAsync(
                phone, new UpsertDeviceDownloadRequest { Kind = kind, PhotoItemId = photo, StoredBytes = 1, StoredAt = DateTimeOffset.UtcNow }, NoVariants));
        }

        // A photo needs its item id.
        Assert.Equal(DeviceDownloadOutcome.BadRequest, await service.UpsertAsync(phone, KeepPhoto(null), NoVariants));
        Assert.Equal(DeviceDownloadOutcome.BadRequest, await service.UpsertAsync(phone, KeepPhoto(Guid.Empty), NoVariants));

        // No kind (an older app) and "variant" are the existing variant report; a variant report ignores any photo id.
        var may = MayDownload();
        Assert.Equal(DeviceDownloadOutcome.Ok, await service.UpsertAsync(phone, Report(film.AssetId, variant, 5), may));
        Assert.Equal(DeviceDownloadOutcome.Ok, await service.UpsertAsync(phone, new UpsertDeviceDownloadRequest
        {
            Kind = "Variant",
            AssetId = film.AssetId,
            VariantId = variant,
            PhotoItemId = Guid.NewGuid(),
            StoredBytes = 6,
            StoredAt = DateTimeOffset.UtcNow,
        }, may));
        var entry = Assert.Single((await service.ListAsync(phone)).Downloads!.Downloads);
        Assert.Equal(DeviceDownloadKinds.Variant, entry.Kind);
        Assert.Null(entry.PhotoItemId);
        Assert.Equal(variant, entry.VariantId);

        // A photo report ignores asset and variant ids.
        Assert.Equal(DeviceDownloadOutcome.Ok, await service.UpsertAsync(phone, new UpsertDeviceDownloadRequest
        {
            Kind = DeviceDownloadKinds.Photo,
            AssetId = film.AssetId,
            VariantId = variant,
            PhotoItemId = photo,
            StoredBytes = 7,
            StoredAt = DateTimeOffset.UtcNow,
        }, NoVariants));
        Assert.Equal(2, (await service.ListAsync(phone)).Downloads!.Downloads.Count);
    }

    [Fact]
    public async Task APhotoThePersonCannotKeep_IsNotFoundOrScopeMissing_AndWithoutPhotoAccessNothingIsKept()
    {
        var photo = InsertSharedPhoto();
        var phone = PhoneAuthority(_mary.Id);
        var access = new SpyPhotoAccess();
        var service = PhotoDownloads(access);

        // Unknown, someone else's and a document are all the one NotFound the access check gives.
        Assert.Equal(DeviceDownloadOutcome.NotFound, await service.UpsertAsync(phone, KeepPhoto(photo), NoVariants));
        Assert.Equal(DeviceDownloadOutcome.NotFound, await service.UpsertAsync(phone, KeepPhoto(Guid.NewGuid()), NoVariants));

        access.ScopeMissing = true;
        Assert.Equal(DeviceDownloadOutcome.PhotoScopeMissing, await service.UpsertAsync(phone, KeepPhoto(photo), NoVariants));
        Assert.Empty((await service.ListAsync(phone)).Downloads!.Downloads);

        // An Engine wired without the photo rules refuses to keep photos at all.
        Assert.Equal(DeviceDownloadOutcome.NotFound, await PhotoDownloads(null).UpsertAsync(phone, KeepPhoto(photo), NoVariants));

        // Only the paired app of a live device of its own account may use the photo routes.
        var browser = new RequestAuthority(PrincipalKind.Human, true, _homeOwner.Id, _mary.Id, DeviceId: _phoneId);
        access.Allow(photo);
        Assert.Equal(DeviceDownloadOutcome.Forbidden, await service.UpsertAsync(browser, KeepPhoto(photo), NoVariants));
        Assert.Equal(DeviceDownloadOutcome.Forbidden, await service.RemovePhotoAsync(browser, photo));
    }

    [Fact]
    public async Task AKeptPhoto_LeavesTheList_WhenAccessIsRemoved_OrThePhotoIsDeleted()
    {
        var gone = InsertSharedPhoto();
        var withdrawn = InsertSharedPhoto();
        var kept = InsertSharedPhoto();
        var access = new SpyPhotoAccess().Allow(gone).Allow(withdrawn).Allow(kept);
        var service = PhotoDownloads(access);
        var phone = PhoneAuthority(_mary.Id);
        foreach (var photo in new[] { gone, withdrawn, kept })
        {
            Assert.Equal(DeviceDownloadOutcome.Ok, await service.UpsertAsync(phone, KeepPhoto(photo), NoVariants));
        }

        Assert.Equal(3, (await service.ListAsync(phone)).Downloads!.Downloads.Count);

        // The photo is deleted: its row goes with it. Another loses its share: it is judged unreadable and left off.
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("DELETE FROM local_items WHERE id = @gone;", new { gone });
        }

        access.Revoke(withdrawn);
        var remaining = (await service.ListAsync(phone)).Downloads!.Downloads;
        Assert.Equal(kept, Assert.Single(remaining).PhotoItemId);

        // Without the photo rules wired, no photo is on the list.
        Assert.Empty((await PhotoDownloads(null).ListAsync(phone)).Downloads!.Downloads);
    }

    [Fact]
    public async Task Photos_AreKeptApartPerPerson_AndNeverShownToAnotherDevice()
    {
        var photo = InsertSharedPhoto();
        var access = new SpyPhotoAccess().Allow(photo);
        var service = PhotoDownloads(access);
        var second = await PairAnotherDeviceAsync();
        Assert.Equal(DeviceDownloadOutcome.Ok, await service.UpsertAsync(PhoneAuthority(_mary.Id), KeepPhoto(photo), NoVariants));

        Assert.Empty((await service.ListAsync(AuthorityOf(second.DeviceId, _mary.Id))).Downloads!.Downloads);
        Assert.Equal(DeviceDownloadOutcome.NotFound, await service.RemovePhotoAsync(AuthorityOf(second.DeviceId, _mary.Id), photo));

        // A different person on the same device starts empty, cannot forget Mary's entry, and may keep the same shared photo themselves.
        await SwitchAsAsync(_mary.Id, _jim.Id, null);
        Assert.Empty((await service.ListAsync(PhoneAuthority(_jim.Id))).Downloads!.Downloads);
        Assert.Equal(DeviceDownloadOutcome.NotFound, await service.RemovePhotoAsync(PhoneAuthority(_jim.Id), photo));
        Assert.Equal(DeviceDownloadOutcome.Ok, await service.UpsertAsync(PhoneAuthority(_jim.Id), KeepPhoto(photo), NoVariants));
        Assert.Equal(DeviceDownloadOutcome.Ok, await service.RemovePhotoAsync(PhoneAuthority(_jim.Id), photo));
        await SwitchAsAsync(_jim.Id, _mary.Id, null);
        Assert.Single((await service.ListAsync(PhoneAuthority(_mary.Id))).Downloads!.Downloads);
    }

    [Fact]
    public async Task PhotosAndVariantsShareTheSame500EntryCap()
    {
        var film = await InsertOwnedMovieAsync("Family film", "PG");
        var photos = new List<Guid>();
        var now = DateTimeOffset.UtcNow.ToString("O");
        InsertSharedPhoto();
        Guid sharedLibrary;
        using (var connection = _database.CreateConnection())
        {
            sharedLibrary = connection.ExecuteScalar<Guid>("SELECT library_id FROM view_shared_library LIMIT 1;");
        }

        using (var connection = _database.CreateConnection())
        using (var transaction = connection.BeginTransaction())
        {
            for (var index = 0; index < DeviceDownloadRepository.MaxRowsPerDevice - 1; index++)
            {
                var id = Guid.NewGuid();
                photos.Add(id);
                connection.Execute(
                    """
                    INSERT INTO local_items (id, scope_kind, library_id, media_kind, primary_file_name, primary_mime_type, created_at, updated_at)
                    VALUES (@id, 'shared', @library, 'image', 'photo.jpg', 'image/jpeg', @now, @now);
                    INSERT INTO device_photo_downloads (device_id, profile_id, item_id, stored_bytes, stored_at, updated_at)
                    VALUES (@device, @profile, @id, 1, @now, @now);
                    """,
                    new { id, library = sharedLibrary, device = _phoneId, profile = _mary.Id, now }, transaction);
            }

            transaction.Commit();
        }

        var access = new SpyPhotoAccess();
        foreach (var photo in photos)
        {
            access.Allow(photo);
        }

        var extraPhoto = InsertSharedPhoto();
        access.Allow(extraPhoto);
        var extraVariant = InsertVariant(film.AssetId, _mary.Id, _phoneId);
        var service = PhotoDownloads(access);
        var phone = PhoneAuthority(_mary.Id);

        // 499 photos leave room for exactly one more entry of either kind.
        Assert.Equal(DeviceDownloadOutcome.Ok, await service.UpsertAsync(phone, Report(film.AssetId, extraVariant, 1), MayDownload()));
        Assert.Equal(DeviceDownloadOutcome.TooMany, await service.UpsertAsync(phone, KeepPhoto(extraPhoto), NoVariants));
        // An entry already on the list can still be updated, and forgetting one makes room again.
        Assert.Equal(DeviceDownloadOutcome.Ok, await service.UpsertAsync(phone, KeepPhoto(photos[0], 99), NoVariants));
        Assert.Equal(DeviceDownloadRepository.MaxRowsPerDevice, (await service.ListAsync(phone)).Downloads!.Downloads.Count);
        Assert.Equal(DeviceDownloadOutcome.Ok, await service.RemovePhotoAsync(phone, photos[1]));
        Assert.Equal(DeviceDownloadOutcome.Ok, await service.UpsertAsync(phone, KeepPhoto(extraPhoto), NoVariants));
    }

    [Fact]
    public async Task AKeptPhotoWhoseFileIsMissingForNow_StaysListed_ButCannotBeKeptAgain()
    {
        var present = InsertSharedPhoto();
        var unmounted = InsertSharedPhoto();
        var access = new SpyPhotoAccess().Allow(present).Allow(unmounted);
        var service = PhotoDownloads(access);
        var phone = PhoneAuthority(_mary.Id);
        Assert.Equal(DeviceDownloadOutcome.Ok, await service.UpsertAsync(phone, KeepPhoto(present), NoVariants));
        Assert.Equal(DeviceDownloadOutcome.Ok, await service.UpsertAsync(phone, KeepPhoto(unmounted), NoVariants));

        // The library volume is unmounted for a while: the photo is still the person's, so the device keeps its copy.
        access.Revoke(unmounted);
        access.MissingFile(unmounted);
        Assert.Equal(2, (await service.ListAsync(phone)).Downloads!.Downloads.Count);
        // It cannot be kept anew while there is nothing to download (the preview route answers 404 too).
        Assert.Equal(DeviceDownloadOutcome.NotFound, await service.UpsertAsync(phone, KeepPhoto(Guid.NewGuid()), NoVariants));
        using (var connection = _database.CreateConnection())
        {
            Assert.Equal(2, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM device_photo_downloads;"));
        }

        // Only a real loss of access (not a missing file) hides it.
        access.Revoke(unmounted);
        Assert.Equal(present, Assert.Single((await service.ListAsync(phone)).Downloads!.Downloads).PhotoItemId);
    }

    [Fact]
    public async Task TheListChecksAllKeptPhotosInOneBulkPass_NotOneByOne()
    {
        var photos = Enumerable.Range(0, 12).Select(_ => InsertSharedPhoto()).ToList();
        var access = new SpyPhotoAccess();
        photos.ForEach(photo => access.Allow(photo));
        var service = PhotoDownloads(access);
        var phone = PhoneAuthority(_mary.Id);
        foreach (var photo in photos)
        {
            Assert.Equal(DeviceDownloadOutcome.Ok, await service.UpsertAsync(phone, KeepPhoto(photo), NoVariants));
        }

        var singleBefore = access.SingleCalls;
        Assert.Equal(12, (await service.ListAsync(phone)).Downloads!.Downloads.Count);
        Assert.Equal(singleBefore, access.SingleCalls);
        Assert.Equal(1, access.ManyCalls);
    }

    [Fact]
    public async Task KeptPhotosTheListHides_DoNotCountTowardsTheCap_ForAVariantOrAPhoto()
    {
        var film = await InsertOwnedMovieAsync("Family film", "PG");
        var photos = new List<Guid>();
        var now = DateTimeOffset.UtcNow.ToString("O");
        InsertSharedPhoto();
        Guid sharedLibrary;
        using (var connection = _database.CreateConnection())
        {
            sharedLibrary = connection.ExecuteScalar<Guid>("SELECT library_id FROM view_shared_library LIMIT 1;");
        }

        using (var connection = _database.CreateConnection())
        using (var transaction = connection.BeginTransaction())
        {
            for (var index = 0; index < DeviceDownloadRepository.MaxRowsPerDevice; index++)
            {
                var id = Guid.NewGuid();
                photos.Add(id);
                connection.Execute(
                    """
                    INSERT INTO local_items (id, scope_kind, library_id, media_kind, primary_file_name, primary_mime_type, created_at, updated_at)
                    VALUES (@id, 'shared', @library, 'image', 'photo.jpg', 'image/jpeg', @now, @now);
                    INSERT INTO device_photo_downloads (device_id, profile_id, item_id, stored_bytes, stored_at, updated_at)
                    VALUES (@device, @profile, @id, 1, @now, @now);
                    """,
                    new { id, library = sharedLibrary, device = _phoneId, profile = _mary.Id, now }, transaction);
            }

            transaction.Commit();
        }

        var access = new SpyPhotoAccess();
        photos.ForEach(photo => access.Allow(photo));
        var service = PhotoDownloads(access);
        var phone = PhoneAuthority(_mary.Id);
        var variant = InsertVariant(film.AssetId, _mary.Id, _phoneId);
        var newPhoto = InsertSharedPhoto();
        access.Allow(newPhoto);

        // Everything listed is still readable: the device is full, and nothing is removed.
        Assert.Equal(DeviceDownloadOutcome.TooMany, await service.UpsertAsync(phone, Report(film.AssetId, variant, 1), MayDownload()));
        Assert.Equal(DeviceDownloadOutcome.TooMany, await service.UpsertAsync(phone, KeepPhoto(newPhoto), NoVariants));
        using (var connection = _database.CreateConnection())
        {
            Assert.Equal(DeviceDownloadRepository.MaxRowsPerDevice, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM device_photo_downloads WHERE device_id = @device;", new { device = _phoneId }));
        }

        // Access to ten of them is withdrawn: the list hides them, so they no longer use up the limit.
        photos.Take(10).ToList().ForEach(access.Revoke);
        Assert.Equal(DeviceDownloadRepository.MaxRowsPerDevice - 10, (await service.ListAsync(phone)).Downloads!.Downloads.Count);
        Assert.Equal(DeviceDownloadOutcome.Ok, await service.UpsertAsync(phone, Report(film.AssetId, variant, 1), MayDownload()));
        using (var connection = _database.CreateConnection())
        {
            Assert.Equal(DeviceDownloadRepository.MaxRowsPerDevice - 10, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM device_photo_downloads WHERE device_id = @device;", new { device = _phoneId }));
        }

        // The hidden rows were cleared, and room for nine more remains.
        Assert.Equal(DeviceDownloadOutcome.Ok, await service.UpsertAsync(phone, KeepPhoto(newPhoto), NoVariants));
        Assert.Equal(DeviceDownloadRepository.MaxRowsPerDevice - 10 + 2, (await service.ListAsync(phone)).Downloads!.Downloads.Count);
    }

    [Fact]
    public async Task APhotosReportedSizeIsClampedToASaneMaximum()
    {
        var photo = InsertSharedPhoto();
        var service = PhotoDownloads(new SpyPhotoAccess().Allow(photo));
        var phone = PhoneAuthority(_mary.Id);

        Assert.Equal(DeviceDownloadOutcome.Ok, await service.UpsertAsync(phone, KeepPhoto(photo, long.MaxValue), NoVariants));
        Assert.Equal(DeviceDownloadService.MaxPhotoStoredBytes, Assert.Single((await service.ListAsync(phone)).Downloads!.Downloads).StoredBytes);
        Assert.Equal(DeviceDownloadOutcome.Ok, await service.UpsertAsync(phone, KeepPhoto(photo, 1_234), NoVariants));
        Assert.Equal(1_234, Assert.Single((await service.ListAsync(phone)).Downloads!.Downloads).StoredBytes);
    }

    [Fact]
    public async Task OverHttp_ABookKindIs400_APhotoWithoutPhotoRulesIs404_AndForgettingAPhotoIsOnTheItsOwnRoute()
    {
        var photo = InsertSharedPhoto();
        using var app = BuildHttpApplication();
        var paired = Assert.IsType<ClientAccessIdentity>(await _authorization.ValidateAccessTokenAsync(_access));
        var pairedUser = ContextFor(paired).User;
        var browser = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(TuvimaClaimTypes.PrincipalKind, PrincipalKind.Human.ToString()),
            new Claim(TuvimaClaimTypes.AccountId, _homeOwner.Id.ToString("D")),
            new Claim(TuvimaClaimTypes.ActiveProfileId, _mary.Id.ToString("D")),
            new Claim(TuvimaClaimTypes.DeviceId, _phoneId.ToString("D")),
        ], "test"));
        const string route = "/api/v1/devices/current/downloads";
        const string photoRoute = "/api/v1/devices/current/downloads/photos/{itemId:guid}";

        Assert.Equal(StatusCodes.Status400BadRequest, (await SendAsync(app, "POST", route, pairedUser,
            new UpsertDeviceDownloadRequest { Kind = DeviceDownloadKinds.Book, PhotoItemId = photo, StoredBytes = 1, StoredAt = DateTimeOffset.UtcNow })).Status);
        Assert.Equal(StatusCodes.Status400BadRequest, (await SendAsync(app, "POST", route, pairedUser,
            new UpsertDeviceDownloadRequest { Kind = DeviceDownloadKinds.Photo, StoredBytes = 1, StoredAt = DateTimeOffset.UtcNow })).Status);
        Assert.Equal(StatusCodes.Status404NotFound, (await SendAsync(app, "POST", route, pairedUser, KeepPhoto(photo))).Status);
        Assert.Equal(StatusCodes.Status403Forbidden, (await SendAsync(app, "POST", route, browser, KeepPhoto(photo))).Status);

        Assert.Equal(StatusCodes.Status404NotFound, (await SendAsync(app, "DELETE", photoRoute, pairedUser, itemId: photo)).Status);
        Assert.Equal(StatusCodes.Status403Forbidden, (await SendAsync(app, "DELETE", photoRoute, browser, itemId: photo)).Status);
    }

    private static ValueTask<bool> NoVariants(Guid assetId, CancellationToken token) => ValueTask.FromResult(false);

    /// <summary>Stands in for the View photo rules (which have their own tests): allows exactly the photos it was told to.</summary>
    private sealed class SpyPhotoAccess : IPhotoDownloadAccess
    {
        private readonly HashSet<Guid> _allowed = [];
        private readonly HashSet<Guid> _fileMissing = [];

        public bool ScopeMissing { get; set; }

        /// <summary>How many times each way of asking was used; the list must use the bulk way only.</summary>
        public int SingleCalls { get; private set; }

        public int ManyCalls { get; private set; }

        public SpyPhotoAccess Allow(Guid itemId)
        {
            _allowed.Add(itemId);
            return this;
        }

        /// <summary>The person may read it, but its file is not on disk right now.</summary>
        public SpyPhotoAccess MissingFile(Guid itemId)
        {
            _fileMissing.Add(itemId);
            return this;
        }

        public void Revoke(Guid itemId)
        {
            _allowed.Remove(itemId);
            _fileMissing.Remove(itemId);
        }

        public ValueTask<PhotoKeepAccess> EvaluateAsync(RequestAuthority authority, Guid itemId, CancellationToken ct = default)
        {
            SingleCalls++;
            return ValueTask.FromResult(ScopeMissing
                ? PhotoKeepAccess.ScopeMissing
                : _allowed.Contains(itemId) ? PhotoKeepAccess.Allowed
                : _fileMissing.Contains(itemId) ? PhotoKeepAccess.FileMissing : PhotoKeepAccess.NotFound);
        }

        public ValueTask<PhotoKeepBulkResult> EvaluateManyAsync(
            RequestAuthority authority, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
        {
            ManyCalls++;
            return ValueTask.FromResult(ScopeMissing
                ? new PhotoKeepBulkResult(true, new HashSet<Guid>())
                : new PhotoKeepBulkResult(false, itemIds.Where(id => _allowed.Contains(id) || _fileMissing.Contains(id)).ToHashSet()));
        }
    }
}
