using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Artwork the workspace linked to a person was stored as <c>/api/v1/display/artwork/assets/{id}/content</c>, which is now an
/// administrator-only route. Person details and the cover redirect read that value, so it is served as the catalogue-checked
/// <c>/stream/artwork/{entity asset id}</c> instead: a Kids and an adult paired token can fetch art of people they may see.
/// </summary>
public sealed partial class DeviceProfileSwitchTests
{
    [Fact]
    public async Task PersonArtStoredTheOldWay_IsServedAsAStreamArtworkUrl_AKidsTokenMayFetchOnlyForVisiblePeople()
    {
        var family = await InsertOwnedMovieAsync("Family film", "PG");
        var adult = await InsertOwnedMovieAsync("Adult film", "R");
        var visiblePerson = await InsertPersonAsync("Ana Family", family.WorkId, family.AssetId);
        var hiddenPerson = await InsertPersonAsync("Rex Adult", adult.WorkId, adult.AssetId);
        var visibleArt = await InsertLinkedHeadshotAsync(visiblePerson);
        var hiddenArt = await InsertLinkedHeadshotAsync(hiddenPerson);

        // What the person details read (the preferred Headshot) is the checked stream URL, never the admin-only content route.
        var repository = new EntityAssetRepository(_database);
        var read = Assert.Single(await repository.GetByEntityAsync(visiblePerson.ToString("D"), "Headshot"));
        Assert.Equal($"/stream/artwork/{visibleArt:D}", read.ImageUrl);
        Assert.Equal(visibleArt, read.Id);
        Assert.Equal($"/stream/artwork/{hiddenArt:D}",
            Assert.Single(await repository.GetByEntityAsync(hiddenPerson.ToString("D"), "Headshot")).ImageUrl);
        using (var connection = _database.CreateConnection())
        {
            Assert.StartsWith("/api/v1/display/artwork/assets/", connection.ExecuteScalar<string>(
                "SELECT image_url FROM entity_assets WHERE id=@id;", new { id = visibleArt }));
        }

        // A URL that is already in the new form, or from a provider, is left alone.
        var currentUrl = $"/stream/artwork/{Guid.NewGuid():D}";
        await InsertHeadshotAsync(visiblePerson, "Banner", currentUrl);
        await InsertHeadshotAsync(visiblePerson, "Logo", "https://img.example/logo.png");
        Assert.Equal(currentUrl, Assert.Single(await repository.GetByEntityAsync(visiblePerson.ToString("D"), "Banner")).ImageUrl);
        Assert.Equal("https://img.example/logo.png",
            Assert.Single(await repository.GetByEntityAsync(visiblePerson.ToString("D"), "Logo")).ImageUrl);

        await new ApplicationRepository(_database).ReplacePermissionsAsync(
            BuiltInApplicationIds.NativeClient,
            new HashSet<ApplicationPermissionId>
            {
                ApplicationPermissionIds.LibraryRead, ApplicationPermissionIds.PlaybackRead,
                ApplicationPermissionIds.DownloadsRead, ApplicationPermissionIds.DownloadsWrite,
                ApplicationPermissionIds.ArtworkRead,
            },
            DateTimeOffset.UtcNow);
        IReadOnlyList<string> scopes = ["library.read", "artwork.read"];
        var adultDevice = await PairAnotherDeviceAsync(scopes);
        var kidsDevice = await PairAnotherDeviceAsync(scopes);
        var kidsAccess = await SwitchDeviceAsync(kidsDevice.DeviceId, kidsDevice.AccessToken, _sam.Id);

        async Task<CatalogueResourceAccess> FetchAsync(string accessToken, Guid variant)
        {
            var context = ContextFor(Assert.IsType<ClientAccessIdentity>(await _authorization.ValidateAccessTokenAsync(accessToken)));
            return await CreateResourceService(context)
                .EvaluateArtworkVariantAsync(context, variant, ApplicationPermissionIds.ArtworkRead);
        }

        Assert.Equal(CatalogueResourceAccess.Allowed, await FetchAsync(adultDevice.AccessToken, visibleArt));
        Assert.Equal(CatalogueResourceAccess.Allowed, await FetchAsync(adultDevice.AccessToken, hiddenArt));
        Assert.Equal(CatalogueResourceAccess.Allowed, await FetchAsync(kidsAccess, visibleArt));
        Assert.NotEqual(CatalogueResourceAccess.Allowed, await FetchAsync(kidsAccess, hiddenArt));
    }

    private async Task<Guid> InsertPersonAsync(string name, Guid workId, Guid assetId)
    {
        var personId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        await connection.ExecuteAsync(
            """
            INSERT INTO persons (id, name, created_at) VALUES (@personId, @name, CURRENT_TIMESTAMP);
            INSERT INTO person_media_links (media_asset_id, person_id, role) VALUES (@assetId, @personId, 'Director');
            INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value) VALUES (@workId, 'director', 0, @name);
            """,
            new { personId, name, workId, assetId });
        return personId;
    }

    private async Task<Guid> InsertLinkedHeadshotAsync(Guid personId) =>
        (await InsertHeadshotAsync(personId, "Headshot",
            $"/api/v1/display/artwork/assets/{Guid.NewGuid():N}/content")).Id;

    private async Task<(Guid Id, string Url)> InsertHeadshotAsync(Guid personId, string assetType, string imageUrl)
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        await connection.ExecuteAsync(
            """
            INSERT INTO entity_assets (
                id, entity_id, entity_type, asset_type, image_url, local_image_path,
                asset_class, storage_location, owner_scope, is_preferred, is_user_override, created_at)
            VALUES (@id, @personId, 'Person', @assetType, @imageUrl, @path,
                    'Artwork', 'Central', 'Person', 1, 1, @now);
            """,
            new
            {
                id,
                personId,
                assetType,
                imageUrl,
                path = Path.Combine(Path.GetTempPath(), $"{id:N}.jpg"),
                now = DateTimeOffset.UtcNow.ToString("O"),
            });
        return (id, imageUrl);
    }
}
