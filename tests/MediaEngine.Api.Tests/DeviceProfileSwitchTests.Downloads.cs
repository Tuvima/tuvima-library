using System.Security.Claims;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using MediaEngine.Storage.Playback;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The paired app's offline download list (<c>/api/v1/devices/current/downloads</c>), run against the same account,
/// profile and paired-phone rig as the profile switch: the device comes from the token only, one device never sees
/// another's list, and a variant this profile cannot download answers exactly like one that does not exist.
/// </summary>
public sealed partial class DeviceProfileSwitchTests
{
    private DeviceDownloadService Downloads() => new(_devices, new DeviceDownloadRepository(_database), TimeProvider.System);

    private static UpsertDeviceDownloadRequest Report(Guid assetId, Guid variantId, long bytes, DateTimeOffset? storedAt = null) => new()
    {
        AssetId = assetId,
        VariantId = variantId,
        StoredBytes = bytes,
        StoredAt = storedAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
    };

    /// <summary>The download route's catalogue check for whoever the device is using now (read when it is called, so it follows a profile switch).</summary>
    private Func<Guid, CancellationToken, ValueTask<bool>> MayDownload() => async (assetId, token) =>
    {
        var context = ContextFor(Assert.IsType<ClientAccessIdentity>(await _authorization.ValidateAccessTokenAsync(_access)));
        var resources = CreateResourceService(context);
        return await resources.EvaluateAssetAsync(context, assetId, ApplicationPermissionIds.DownloadsRead, token) == CatalogueResourceAccess.Allowed;
    };

    private Guid InsertVariant(
        Guid assetId,
        Guid ownerProfile,
        Guid ownerDevice,
        string status = OfflineVariantStatuses.Ready,
        long size = 1_000,
        DateTimeOffset? expiresAt = null)
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO offline_variants
                (id, asset_id, owner_profile_id, owner_device_id, profile_key, source_hash, display_name, status, output_path, file_size, created_at, expires_at)
            VALUES
                (@id, @assetId, @ownerProfile, @ownerDevice, 'mobile-small', 'hash', 'Phone', @status, @path, @size, @now, @expires);
            """,
            new
            {
                id,
                assetId,
                ownerProfile,
                ownerDevice,
                status,
                path = Path.Combine(Path.GetTempPath(), $"{id:N}.mp4"),
                size,
                now = DateTimeOffset.UtcNow.ToString("O"),
                expires = expiresAt?.ToString("O"),
            });
        return id;
    }

    private RequestAuthority AuthorityOf(Guid deviceId, Guid profileId) =>
        new(PrincipalKind.DelegatedUserClient, true, _homeOwner.Id, profileId,
            ApplicationId: BuiltInApplicationIds.NativeClient, DeviceId: deviceId);

    [Fact]
    public async Task Downloads_AreReportedListedUpdatedClampedAndForgotten_PerVariant()
    {
        var film = await InsertOwnedMovieAsync("Family film", "PG");
        var expires = DateTimeOffset.UtcNow.AddDays(30);
        var variant = InsertVariant(film.AssetId, _mary.Id, _phoneId, size: 1_000, expiresAt: expires);
        var service = Downloads();
        var may = MayDownload();
        var storedAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        Assert.Empty((await service.ListAsync(PhoneAuthority(_mary.Id))).Downloads!.Downloads);
        Assert.Equal(DeviceDownloadOutcome.Ok, await service.UpsertAsync(PhoneAuthority(_mary.Id), Report(film.AssetId, variant, 600, storedAt), may));

        var entry = Assert.Single((await service.ListAsync(PhoneAuthority(_mary.Id))).Downloads!.Downloads);
        Assert.Equal(film.AssetId, entry.AssetId);
        Assert.Equal(variant, entry.VariantId);
        Assert.Equal(600, entry.StoredBytes);
        Assert.Equal(storedAt, entry.StoredAt);
        Assert.Equal(expires, entry.ExpiresAt);

        // Reporting the same variant again updates the one entry. A device cannot claim more than the variant holds.
        Assert.Equal(DeviceDownloadOutcome.Ok, await service.UpsertAsync(PhoneAuthority(_mary.Id), Report(film.AssetId, variant, 5_000), may));
        Assert.Equal(1_000, Assert.Single((await service.ListAsync(PhoneAuthority(_mary.Id))).Downloads!.Downloads).StoredBytes);
        Assert.Equal(DeviceDownloadOutcome.Ok, await service.UpsertAsync(PhoneAuthority(_mary.Id), Report(film.AssetId, variant, -7), may));
        Assert.Equal(0, Assert.Single((await service.ListAsync(PhoneAuthority(_mary.Id))).Downloads!.Downloads).StoredBytes);

        // A stored time later than the server's clock is the server's clock.
        var before = DateTimeOffset.UtcNow;
        Assert.Equal(DeviceDownloadOutcome.Ok, await service.UpsertAsync(
            PhoneAuthority(_mary.Id), Report(film.AssetId, variant, 10, DateTimeOffset.UtcNow.AddYears(1)), may));
        Assert.InRange(
            Assert.Single((await service.ListAsync(PhoneAuthority(_mary.Id))).Downloads!.Downloads).StoredAt,
            before, DateTimeOffset.UtcNow);

        Assert.Equal(DeviceDownloadOutcome.Ok, await service.RemoveAsync(PhoneAuthority(_mary.Id), variant));
        Assert.Empty((await service.ListAsync(PhoneAuthority(_mary.Id))).Downloads!.Downloads);
        Assert.Equal(DeviceDownloadOutcome.NotFound, await service.RemoveAsync(PhoneAuthority(_mary.Id), variant));
    }

    [Fact]
    public async Task AVariantThatIsNotThisDevicesReadyUnexpiredOwnVariant_IsNotFound_LikeAnUnknownOne()
    {
        var film = await InsertOwnedMovieAsync("Family film", "PG");
        var other = await InsertOwnedMovieAsync("Other film", "PG");
        var service = Downloads();
        var may = MayDownload();
        var second = await PairAnotherDeviceAsync();

        var unknown = await service.UpsertAsync(PhoneAuthority(_mary.Id), Report(film.AssetId, Guid.NewGuid(), 1), may);
        var someoneElses = InsertVariant(film.AssetId, _jim.Id, _phoneId);
        var otherDevices = InsertVariant(film.AssetId, _mary.Id, second.DeviceId);
        var notReady = InsertVariant(film.AssetId, _mary.Id, _phoneId, OfflineVariantStatuses.Pending);
        var expired = InsertVariant(film.AssetId, _mary.Id, _phoneId, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var wrongAsset = InsertVariant(other.AssetId, _mary.Id, _phoneId);
        var good = InsertVariant(film.AssetId, _mary.Id, _phoneId);

        Assert.Equal(DeviceDownloadOutcome.NotFound, unknown);
        foreach (var variant in new[] { someoneElses, otherDevices, notReady, expired })
        {
            Assert.Equal(DeviceDownloadOutcome.NotFound, await service.UpsertAsync(PhoneAuthority(_mary.Id), Report(film.AssetId, variant, 1), may));
        }

        // A real variant of another asset, named under the wrong asset id, is no better than an unknown one.
        Assert.Equal(DeviceDownloadOutcome.NotFound, await service.UpsertAsync(PhoneAuthority(_mary.Id), Report(film.AssetId, wrongAsset, 1), may));
        Assert.Equal(DeviceDownloadOutcome.NotFound, await service.UpsertAsync(PhoneAuthority(_mary.Id), Report(Guid.Empty, good, 1), may));
        Assert.Equal(DeviceDownloadOutcome.NotFound, await service.UpsertAsync(PhoneAuthority(_mary.Id), Report(film.AssetId, Guid.Empty, 1), may));
        Assert.Empty((await service.ListAsync(PhoneAuthority(_mary.Id))).Downloads!.Downloads);
        Assert.Equal(DeviceDownloadOutcome.Ok, await service.UpsertAsync(PhoneAuthority(_mary.Id), Report(film.AssetId, good, 1), may));
    }

    [Fact]
    public async Task AKidsProfileCannotListAnRRatedVariant_ThoughItsOwnerAndDevice()
    {
        var family = await InsertOwnedMovieAsync("Family film", "PG");
        var adult = await InsertOwnedMovieAsync("Adult film", "R");
        var kids = (await SwitchAsAsync(_mary.Id, _sam.Id, null)).Tokens!;
        var adultVariant = InsertVariant(adult.AssetId, _sam.Id, _phoneId);
        var familyVariant = InsertVariant(family.AssetId, _sam.Id, _phoneId);
        var service = Downloads();
        var may = MayDownload();

        var refused = await service.UpsertAsync(PhoneAuthority(_sam.Id), Report(adult.AssetId, adultVariant, 1), may);
        var unknown = await service.UpsertAsync(PhoneAuthority(_sam.Id), Report(adult.AssetId, Guid.NewGuid(), 1), may);

        Assert.Equal(DeviceDownloadOutcome.NotFound, refused);
        Assert.Equal(unknown, refused);
        Assert.Equal(DeviceDownloadOutcome.Ok, await service.UpsertAsync(PhoneAuthority(_sam.Id), Report(family.AssetId, familyVariant, 1), may));
        Assert.Equal(_sam.Id, kids.ProfileId);
    }

    [Fact]
    public async Task OnlyThePairedAppsOwnLiveDevice_MayUseTheList()
    {
        var film = await InsertOwnedMovieAsync("Family film", "PG");
        var variant = InsertVariant(film.AssetId, _mary.Id, _phoneId);
        var service = Downloads();
        var may = MayDownload();
        var browser = new RequestAuthority(PrincipalKind.Human, true, _homeOwner.Id, _mary.Id, DeviceId: _phoneId);
        var unknownDevice = AuthorityOf(Guid.NewGuid(), _mary.Id);
        var otherAccount = new RequestAuthority(
            PrincipalKind.DelegatedUserClient, true, Guid.NewGuid(), _bea.Id, DeviceId: _phoneId);
        var service2 = new RequestAuthority(PrincipalKind.ServiceApplication, true, ApplicationId: BuiltInApplicationIds.NativeClient, DeviceId: _phoneId);
        // The token's profile is not the one the device is on any more (a stale token after a switch).
        var staleProfile = PhoneAuthority(_jim.Id);

        foreach (var caller in new[] { browser, unknownDevice, otherAccount, service2, staleProfile })
        {
            Assert.Null((await service.ListAsync(caller)).Downloads);
            Assert.Equal(DeviceDownloadOutcome.Forbidden, await service.UpsertAsync(caller, Report(film.AssetId, variant, 1), may));
            Assert.Equal(DeviceDownloadOutcome.Forbidden, await service.RemoveAsync(caller, variant));
        }

        Assert.Empty((await service.ListAsync(PhoneAuthority(_mary.Id))).Downloads!.Downloads);
    }

    [Fact]
    public async Task OneDeviceNeverSeesAnotherDevicesList_AndTheListFollowsTheProfile()
    {
        var film = await InsertOwnedMovieAsync("Family film", "PG");
        var variant = InsertVariant(film.AssetId, _mary.Id, _phoneId);
        var second = await PairAnotherDeviceAsync();
        var secondPhone = AuthorityOf(second.DeviceId, _mary.Id);
        var service = Downloads();
        var may = MayDownload();
        Assert.Equal(DeviceDownloadOutcome.Ok, await service.UpsertAsync(PhoneAuthority(_mary.Id), Report(film.AssetId, variant, 10), may));

        Assert.Empty((await service.ListAsync(secondPhone)).Downloads!.Downloads);
        // The second phone can neither claim the first phone's variant nor forget its entry.
        Assert.Equal(DeviceDownloadOutcome.NotFound, await service.UpsertAsync(secondPhone, Report(film.AssetId, variant, 10), may));
        Assert.Equal(DeviceDownloadOutcome.NotFound, await service.RemoveAsync(secondPhone, variant));
        Assert.Single((await service.ListAsync(PhoneAuthority(_mary.Id))).Downloads!.Downloads);

        // Switching person on the same device: the new person starts with an empty list, and the old person's stays put.
        await SwitchAsAsync(_mary.Id, _jim.Id, null);
        Assert.Empty((await service.ListAsync(PhoneAuthority(_jim.Id))).Downloads!.Downloads);
        Assert.Equal(DeviceDownloadOutcome.NotFound, await service.RemoveAsync(PhoneAuthority(_jim.Id), variant));
        await SwitchAsAsync(_jim.Id, _mary.Id, null);
        Assert.Single((await service.ListAsync(PhoneAuthority(_mary.Id))).Downloads!.Downloads);
    }

    [Fact]
    public async Task ADeviceHoldsAtMost500Downloads_ButExistingOnesStillUpdate()
    {
        var film = await InsertOwnedMovieAsync("Family film", "PG");
        var variants = new List<Guid>();
        using (var connection = _database.CreateConnection())
        using (var transaction = connection.BeginTransaction())
        {
            for (var index = 0; index < DeviceDownloadRepository.MaxRowsPerDevice; index++)
            {
                var id = Guid.NewGuid();
                variants.Add(id);
                connection.Execute(
                    """
                    INSERT INTO offline_variants (id, asset_id, owner_profile_id, owner_device_id, profile_key, source_hash, display_name, status, file_size, created_at)
                    VALUES (@id, @asset, @profile, @device, 'mobile-small', 'hash', 'Phone', 'ready', 100, @now);
                    INSERT INTO device_downloads (device_id, profile_id, asset_id, variant_id, stored_bytes, stored_at, updated_at)
                    VALUES (@device, @profile, @asset, @id, 1, @now, @now);
                    """,
                    new { id, asset = film.AssetId, profile = _mary.Id, device = _phoneId, now = DateTimeOffset.UtcNow.ToString("O") }, transaction);
            }

            transaction.Commit();
        }

        var service = Downloads();
        var may = MayDownload();
        var extra = InsertVariant(film.AssetId, _mary.Id, _phoneId);

        Assert.Equal(DeviceDownloadOutcome.TooMany, await service.UpsertAsync(PhoneAuthority(_mary.Id), Report(film.AssetId, extra, 1), may));
        Assert.Equal(DeviceDownloadOutcome.Ok, await service.UpsertAsync(PhoneAuthority(_mary.Id), Report(film.AssetId, variants[0], 50), may));
        Assert.Equal(DeviceDownloadRepository.MaxRowsPerDevice, (await service.ListAsync(PhoneAuthority(_mary.Id))).Downloads!.Downloads.Count);

        Assert.Equal(DeviceDownloadOutcome.Ok, await service.RemoveAsync(PhoneAuthority(_mary.Id), variants[1]));
        Assert.Equal(DeviceDownloadOutcome.Ok, await service.UpsertAsync(PhoneAuthority(_mary.Id), Report(film.AssetId, extra, 1), may));
    }

    [Fact]
    public async Task RemovingAVariant_TakesItsDeviceListEntryWithIt()
    {
        var film = await InsertOwnedMovieAsync("Family film", "PG");
        var variant = InsertVariant(film.AssetId, _mary.Id, _phoneId);
        var service = Downloads();
        Assert.Equal(DeviceDownloadOutcome.Ok, await service.UpsertAsync(
            PhoneAuthority(_mary.Id), Report(film.AssetId, variant, 10), MayDownload()));

        await new PlaybackStateRepository(_database).DeleteOfflineVariantAsync(variant);

        Assert.Empty((await service.ListAsync(PhoneAuthority(_mary.Id))).Downloads!.Downloads);
        using var connection = _database.CreateConnection();
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM device_downloads WHERE variant_id = @variant;", new { variant }));
    }

    [Fact]
    public async Task OverHttp_OnlyThePairedApp_UsesTheThreeRoutes_AndAForeignVariantIs404()
    {
        var film = await InsertOwnedMovieAsync("Family film", "PG");
        var variant = InsertVariant(film.AssetId, _mary.Id, _phoneId, size: 4_000);
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
        var report = new UpsertDeviceDownloadRequest { AssetId = film.AssetId, VariantId = variant, StoredBytes = 9_999, StoredAt = DateTimeOffset.UtcNow };

        Assert.Equal(StatusCodes.Status403Forbidden, (await SendAsync(app, "GET", "/api/v1/devices/current/downloads", browser)).Status);
        Assert.Equal(StatusCodes.Status403Forbidden, (await SendAsync(app, "POST", "/api/v1/devices/current/downloads", browser, report)).Status);
        Assert.Equal(StatusCodes.Status403Forbidden, (await SendAsync(app, "DELETE", "/api/v1/devices/current/downloads/{variantId:guid}", browser, variantId: variant)).Status);

        var empty = await SendAsync(app, "GET", "/api/v1/devices/current/downloads", pairedUser);
        Assert.Equal(StatusCodes.Status200OK, empty.Status);
        Assert.Contains("\"downloads\":[]", empty.Body);

        Assert.Equal(StatusCodes.Status204NoContent, (await SendAsync(app, "POST", "/api/v1/devices/current/downloads", pairedUser, report)).Status);
        var listed = await SendAsync(app, "GET", "/api/v1/devices/current/downloads", pairedUser);
        Assert.Contains($"\"variant_id\":\"{variant:D}\"", listed.Body);
        Assert.Contains("\"stored_bytes\":4000", listed.Body);

        var foreign = await SendAsync(app, "POST", "/api/v1/devices/current/downloads", pairedUser, report.With(Guid.NewGuid()));
        Assert.Equal(StatusCodes.Status404NotFound, foreign.Status);

        Assert.Equal(StatusCodes.Status204NoContent, (await SendAsync(app, "DELETE", "/api/v1/devices/current/downloads/{variantId:guid}", pairedUser, variantId: variant)).Status);
        Assert.Equal(StatusCodes.Status404NotFound, (await SendAsync(app, "DELETE", "/api/v1/devices/current/downloads/{variantId:guid}", pairedUser, variantId: variant)).Status);
    }

    private static async Task<(int Status, string Body)> SendAsync(
        WebApplication app, string method, string route, ClaimsPrincipal user, object? body = null, Guid? variantId = null)
    {
        var endpoint = Assert.Single(EndpointsFor(app), candidate =>
            candidate.RoutePattern.RawText == route &&
            candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) == true);
        var payload = body is null ? [] : JsonSerializer.SerializeToUtf8Bytes(body, body.GetType());
        var response = new MemoryStream();
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services,
            User = user,
            Request = { Method = method, ContentType = body is null ? null : "application/json", ContentLength = payload.Length, Body = new MemoryStream(payload) },
            Response = { Body = response },
        };
        if (variantId is { } id)
        {
            context.Request.RouteValues = new RouteValueDictionary { ["variantId"] = id.ToString("D") };
        }

        context.Features.Set<IHttpRequestBodyDetectionFeature>(new RequestBodyDetectionFeature());
        ((HttpContextAccessor)app.Services.GetRequiredService<IHttpContextAccessor>()).HttpContext = context;
        await endpoint.RequestDelegate!(context);
        response.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(response).ReadToEndAsync());
    }
}

internal static class UpsertDeviceDownloadRequestExtensions
{
    internal static UpsertDeviceDownloadRequest With(this UpsertDeviceDownloadRequest request, Guid variantId) => new()
    {
        AssetId = request.AssetId,
        VariantId = variantId,
        StoredBytes = request.StoredBytes,
        StoredAt = request.StoredAt,
    };
}
