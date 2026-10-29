using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

// Library access, LA1: a library hidden for a household is absent for every profile in it and present elsewhere, and server
// administrators still see everything. Every viewer-facing read goes through the one rule, so lists, counts, search, detail,
// streams and artwork all agree.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private LibraryVisibilityRule SelectedAwareRule() => new(
        new ProfileAccessRepository(_database), new HouseholdLibraryAccessRepository(_database), new VaultLibraryRepository(_database));

    private Guid HouseholdOf(Guid account)
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<Guid>("SELECT household_id FROM profiles WHERE id=@id;", new { id = ProfileOf(account) });
    }

    private Task HideForHouseholdAsync(Guid household, params Guid[] libraries) =>
        new HouseholdLibraryAccessRepository(_database).ReplaceHiddenLibrariesAsync(household, libraries.ToHashSet(), DateTimeOffset.UtcNow, null);

    [Fact]
    public async Task ALibraryHiddenForTheHousehold_IsAbsentFromListsCountsStreamsAndArtwork_ForItsProfiles_ButNotForAnAdministrator()
    {
        var standardLibrary = Guid.NewGuid();
        var hiddenLibrary = Guid.NewGuid();
        var person = Guid.NewGuid();
        var administrator = Guid.NewGuid();
        await CreateHumanAsync(person, WatchOnly, new HashSet<Guid>());
        await CreateHumanAsync(administrator, EveryFeature, new HashSet<Guid>(), administrator: true);
        await HideForHouseholdAsync(HouseholdOf(person), hiddenLibrary);
        var standard = await InsertOwnedWorkWithIdAsync(standardLibrary, "Standard film", "Movie");
        var hidden = await InsertOwnedWorkWithIdAsync(hiddenLibrary, "Hidden film", "Movie");

        var context = SeedProfileContext(person);
        var service = CreateService(context, RealReaders());
        Assert.Equal([standard.WorkId], (await service.LoadWorksAsync(default)).Select(row => row.WorkId).ToArray());
        Assert.Equal([standard.WorkId], (await service.LoadHomeWorksAsync(default)).Select(row => row.WorkId).ToArray());
        Assert.Equal([standard.WorkId], (await service.LoadVisibleWorkIdsAsync(default)).ToArray());
        Assert.Equal([standard.WorkId], (await service.LoadWorkLibraryRowsAsync(default)).Select(row => row.WorkId).ToArray());
        Assert.False((await service.LoadLibraryScopeAsync(default)).Allows(hiddenLibrary));
        Assert.True((await service.LoadLibraryScopeAsync(default)).Allows(standardLibrary));
        Assert.False((await service.LoadLibraryAccessAsync(default)).Allows(hiddenLibrary.ToString("D"), "Movie"));
        Assert.DoesNotContain(hidden.WorkId, (await service.LoadDetailWorksAsync(hidden.WorkId, default)).Select(row => row.WorkId));
        foreach (var permission in new[] { ApplicationPermissionIds.PlaybackRead, ApplicationPermissionIds.ArtworkRead })
        {
            Assert.Equal(CatalogueResourceAccess.Denied,
                await CreateResourceService(context).EvaluateAssetAsync(context, hidden.AssetId, permission));
            Assert.Equal(CatalogueResourceAccess.Allowed,
                await CreateResourceService(context).EvaluateAssetAsync(context, standard.AssetId, permission));
        }

        var administratorService = CreateService(SeedProfileContext(administrator), RealReaders());
        Assert.Equal(new[] { standard.WorkId, hidden.WorkId }.Order(), (await administratorService.LoadWorksAsync(default)).Select(row => row.WorkId).Order());
    }

    [Fact]
    public async Task ALibraryHiddenForTheHousehold_ReturnsOnceTheHouseholdShowsItAgain()
    {
        var library = Guid.NewGuid();
        var person = Guid.NewGuid();
        await CreateHumanAsync(person, WatchOnly, new HashSet<Guid>());
        var film = await InsertOwnedWorkWithIdAsync(library, "A film", "Movie");
        await HideForHouseholdAsync(HouseholdOf(person), library);
        Assert.Empty(await CreateService(SeedProfileContext(person), RealReaders()).LoadWorksAsync(default));

        await HideForHouseholdAsync(HouseholdOf(person));

        Assert.Equal([film.WorkId],
            (await CreateService(SeedProfileContext(person), RealReaders()).LoadWorksAsync(default)).Select(row => row.WorkId).ToArray());
    }

    [Fact]
    public async Task AProfileHiddenFromOneLibrary_KeepsTheRest_WhileTheHouseholdHidesAnother()
    {
        var householdHidden = Guid.NewGuid();
        var profileHidden = Guid.NewGuid();
        var open = Guid.NewGuid();
        var person = Guid.NewGuid();
        await CreateHumanAsync(person, WatchOnly, new HashSet<Guid> { profileHidden });
        await HideForHouseholdAsync(HouseholdOf(person), householdHidden);
        await InsertOwnedWorkWithIdAsync(householdHidden, "Household hidden", "Movie");
        await InsertOwnedWorkWithIdAsync(profileHidden, "Profile hidden", "Movie");
        var visible = await InsertOwnedWorkWithIdAsync(open, "Open film", "Movie");

        Assert.Equal([visible.WorkId],
            (await CreateService(SeedProfileContext(person), RealReaders()).LoadWorksAsync(default)).Select(row => row.WorkId).ToArray());
    }
}
