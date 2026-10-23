using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using Microsoft.AspNetCore.Http;

namespace MediaEngine.Api.Tests;

// Simpler access (D2, D11): a person sees every library in the areas they have unless an administrator hid it from them.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private static readonly HashSet<AccountFeatureId> WatchOnly = [AccountFeatureId.Watch];

    private DefaultHttpContext SeedProfileContext(Guid account) =>
        HumanContext(account, MediaEngine.Domain.Aggregates.Profile.SeedProfileId);

    [Fact]
    public async Task ANewLibrary_IsVisibleToAnAccountWithItsArea_WithNoRowWrittenForIt()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, WatchOnly, new HashSet<Guid>());
        var brandNewLibrary = Guid.NewGuid();
        var film = await InsertOwnedWorkWithIdAsync(brandNewLibrary, "Brand new film", "Movie");
        var context = SeedProfileContext(account);
        var service = CreateService(context, RealReaders());

        Assert.Equal([film.WorkId], (await service.LoadWorksAsync(default)).Select(row => row.WorkId).ToArray());
        Assert.True((await service.LoadLibraryScopeAsync(default)).Allows(brandNewLibrary));
        Assert.True((await service.LoadLibraryAccessAsync(default)).Allows(brandNewLibrary.ToString("D"), "Movie"));
        Assert.Equal(CatalogueResourceAccess.Allowed,
            await CreateResourceService(context).EvaluateAssetAsync(context, film.AssetId, ApplicationPermissionIds.PlaybackRead));
        using var connection = _database.CreateConnection();
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM account_library_hidden;"));
    }

    [Fact]
    public async Task ALibraryHiddenFromOneAccount_IsAbsentFromItsListsCountsStreamsAndArtwork_ButStillVisibleToAnotherAccountWithTheSameArea()
    {
        var shownLibrary = Guid.NewGuid();
        var hiddenLibrary = Guid.NewGuid();
        var sam = Guid.NewGuid();
        var other = Guid.NewGuid();
        await CreateHumanAsync(sam, WatchOnly, new HashSet<Guid> { hiddenLibrary });
        await CreateHumanAsync(other, WatchOnly, new HashSet<Guid>());
        var shown = await InsertOwnedWorkWithIdAsync(shownLibrary, "Shown film", "Movie");
        var secret = await InsertOwnedWorkWithIdAsync(hiddenLibrary, "Hidden film", "Movie");
        var samContext = SeedProfileContext(sam);
        var otherContext = SeedProfileContext(other);

        var samService = CreateService(samContext, RealReaders());
        Assert.Equal([shown.WorkId], (await samService.LoadWorksAsync(default)).Select(row => row.WorkId).ToArray());
        Assert.Equal([shown.WorkId], (await samService.LoadVisibleWorkIdsAsync(default)).ToArray());
        Assert.Equal([shown.WorkId], (await samService.LoadWorkLibraryRowsAsync(default)).Select(row => row.WorkId).ToArray());
        Assert.False((await samService.LoadLibraryScopeAsync(default)).Allows(hiddenLibrary));
        Assert.False((await samService.LoadLibraryAccessAsync(default)).Allows(hiddenLibrary.ToString("D"), "Movie"));
        Assert.DoesNotContain(secret.WorkId, (await samService.LoadDetailWorksAsync(secret.WorkId, default)).Select(row => row.WorkId));

        // The player, the stream and the artwork all ask the same question about the file's library.
        foreach (var permission in new[] { ApplicationPermissionIds.PlaybackRead, ApplicationPermissionIds.ArtworkRead })
        {
            Assert.Equal(CatalogueResourceAccess.Denied,
                await CreateResourceService(samContext).EvaluateAssetAsync(samContext, secret.AssetId, permission));
            Assert.Equal(CatalogueResourceAccess.Allowed,
                await CreateResourceService(samContext).EvaluateAssetAsync(samContext, shown.AssetId, permission));
        }

        var otherService = CreateService(otherContext, RealReaders());
        Assert.Equal(new[] { shown.WorkId, secret.WorkId }.Order(), (await otherService.LoadWorksAsync(default)).Select(row => row.WorkId).Order());
        Assert.Equal(CatalogueResourceAccess.Allowed,
            await CreateResourceService(otherContext).EvaluateAssetAsync(otherContext, secret.AssetId, ApplicationPermissionIds.PlaybackRead));
    }

    [Fact]
    public async Task AnAccountWithoutTheArea_DoesNotSeeTheLibrary_EvenWithNothingHidden()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, new HashSet<AccountFeatureId> { AccountFeatureId.Read }, new HashSet<Guid>());
        var library = Guid.NewGuid();
        var film = await InsertOwnedWorkWithIdAsync(library, "A film", "Movie");
        var context = SeedProfileContext(account);
        var service = CreateService(context, RealReaders());

        Assert.Empty(await service.LoadWorksAsync(default));
        Assert.False((await service.LoadLibraryAccessAsync(default)).Allows(library.ToString("D"), "Movie"));
        Assert.Equal(CatalogueResourceAccess.Denied,
            await CreateResourceService(context).EvaluateAssetAsync(context, film.AssetId, ApplicationPermissionIds.PlaybackRead));
    }

    [Fact]
    public async Task AnAdministrator_SeesALibrary_EvenWhenAHiddenRowExistsForThem()
    {
        var library = Guid.NewGuid();
        var administrator = Guid.NewGuid();
        await CreateHumanAsync(administrator, EveryFeature, new HashSet<Guid> { library }, administrator: true);
        var film = await InsertOwnedWorkWithIdAsync(library, "Admin film", "Movie");
        var context = SeedProfileContext(administrator);
        var service = CreateService(context, RealReaders());

        Assert.Equal([film.WorkId], (await service.LoadWorksAsync(default)).Select(row => row.WorkId).ToArray());
        Assert.True((await service.LoadLibraryScopeAsync(default)).Allows(library));
        Assert.Equal(CatalogueResourceAccess.Allowed,
            await CreateResourceService(context).EvaluateAssetAsync(context, film.AssetId, ApplicationPermissionIds.PlaybackRead));
    }

    [Fact]
    public async Task AHouseholdMember_SeesNothingInALibraryTheAccountItInheritsFromHides()
    {
        var hiddenLibrary = Guid.NewGuid();
        var primary = Guid.NewGuid();
        var member = Guid.NewGuid();
        await CreateHumanAsync(primary, WatchOnly, new HashSet<Guid> { hiddenLibrary });
        await CreateHumanAsync(member, WatchOnly, new HashSet<Guid>());
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "UPDATE accounts SET grants_inherit_from_account_id=@primary WHERE id=@member;",
                new { primary, member });
        }

        var shown = await InsertOwnedWorkWithIdAsync(Guid.NewGuid(), "Shown film", "Movie");
        await InsertOwnedWorkWithIdAsync(hiddenLibrary, "Hidden film", "Movie");
        var service = CreateService(SeedProfileContext(member), RealReaders());

        Assert.Equal([shown.WorkId], (await service.LoadWorksAsync(default)).Select(row => row.WorkId).ToArray());
    }

    [Fact]
    public async Task TheHiddenLibrariesAreReadOncePerRequest_AndAChangeIsSeenByTheNextRequest()
    {
        var library = Guid.NewGuid();
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, WatchOnly, new HashSet<Guid>());
        var film = await InsertOwnedWorkWithIdAsync(library, "A film", "Movie");
        var thisRequest = CreateService(SeedProfileContext(account), RealReaders());
        Assert.Equal([film.WorkId], (await thisRequest.LoadWorksAsync(default)).Select(row => row.WorkId).ToArray());

        await HideLibraryFromAsync(account, library);

        // The first request keeps the set it read; the next request reads it again and no longer sees the film.
        Assert.Equal([film.WorkId], (await thisRequest.LoadWorksAsync(default)).Select(row => row.WorkId).ToArray());
        var nextRequest = CreateService(SeedProfileContext(account), RealReaders());
        Assert.Empty(await nextRequest.LoadWorksAsync(default));
    }

    [Fact]
    public async Task AVaultCopyOfAFile_NeverReachesTheViewAllFormatFilter_ForANonAdministrator()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var scope = await CreateService(VaultContext(shape.Account, session), RealReaders()).LoadLibraryScopeAsync(default);
        var work = Guid.NewGuid();
        IReadOnlyDictionary<(Guid WorkId, Guid LibraryId), IReadOnlySet<string>> formats =
            new Dictionary<(Guid WorkId, Guid LibraryId), IReadOnlySet<string>>
            {
                [(work, shape.NormalLibrary)] = new HashSet<string> { "hd" },
                [(work, shape.VaultLibrary)] = new HashSet<string> { "4k" },
            };

        Assert.False(scope.Allows(shape.VaultLibrary));
        Assert.True(scope.Allows(shape.NormalLibrary));
        var seen = MediaEngine.Api.Services.Display.DisplayFormatReader.ForViewer(formats, new HashSet<Guid> { work }, scope);
        Assert.Equal(["hd"], seen[work].Order());
    }

    [Fact]
    public async Task TheLibraryScopeOfAnAdministrator_IsUnchangedByTheVault()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var scope = await CreateService(VaultContext(shape.Account, NewVaultSession(shape.Account)), RealReaders()).LoadLibraryScopeAsync(default);

        Assert.True(scope.Allows(shape.VaultLibrary));
        Assert.True(scope.Allows(shape.NormalLibrary));
    }

    [Fact]
    public async Task Vault_AnAdministratorWithVaultUnlocked_StillOpensVaultFilesThroughTheVaultRules_NotTheLibraryRule()
    {
        // Administrators never went through a library grant for the Vault: the Vault branch of the catalogue rule (unlocked session
        // and the Watch area) decides, and the administrator bypass of the library check is kept. The visibility rule changed neither.
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: true);
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var context = VaultContext(shape.Account, session);
        var service = CreateService(context, RealReaders());

        Assert.Contains(shape.Secret.AssetId, (await service.LoadAuthorizedAssetsAsync(default)).Select(row => row.AssetId));
        Assert.Equal(CatalogueResourceAccess.Allowed,
            await CreateResourceService(context).EvaluateAssetAsync(context, shape.Secret.AssetId, ApplicationPermissionIds.PlaybackRead));
        Assert.Equal(CatalogueResourceAccess.Allowed,
            await CreateResourceService(context).EvaluateAssetAsync(context, shape.Normal.AssetId, ApplicationPermissionIds.PlaybackRead));
    }
}
