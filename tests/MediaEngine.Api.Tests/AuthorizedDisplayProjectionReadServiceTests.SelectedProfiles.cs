using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

// Library pages D9, WP6: a library whose access is "Selected profiles" is visible to server administrators and to the profiles an administrator
// chose, and to nobody else. Every viewer-facing read goes through the one rule, so lists, counts, search, streams and artwork all agree.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private readonly FixedLibraryAccessCatalog _selectedLibraries = new();

    private LibraryVisibilityRule SelectedAwareRule() => new(
        new ProfileAccessRepository(_database), new VaultLibraryRepository(_database), new LibraryProfileGrantRepository(_database), _selectedLibraries);

    private Task GrantAsync(Guid library, params Guid[] profiles) =>
        new LibraryProfileGrantRepository(_database).ReplaceGrantedProfilesAsync(library, profiles.ToHashSet(), DateTimeOffset.UtcNow, null);

    [Fact]
    public async Task ASelectedLibrary_IsAbsentFromListsCountsStreamsAndArtwork_ForAProfileNotChosen_ButNotForOneChosenOrAnAdministrator()
    {
        var standardLibrary = Guid.NewGuid();
        var selectedLibrary = Guid.NewGuid();
        _selectedLibraries.Selected.Add(selectedLibrary);
        var notChosen = Guid.NewGuid();
        var chosen = Guid.NewGuid();
        var administrator = Guid.NewGuid();
        await CreateHumanAsync(notChosen, WatchOnly, new HashSet<Guid>());
        await CreateHumanAsync(chosen, WatchOnly, new HashSet<Guid>());
        await CreateHumanAsync(administrator, EveryFeature, new HashSet<Guid>(), administrator: true);
        await GrantAsync(selectedLibrary, ProfileOf(chosen));
        var standard = await InsertOwnedWorkWithIdAsync(standardLibrary, "Standard film", "Movie");
        var selected = await InsertOwnedWorkWithIdAsync(selectedLibrary, "Selected film", "Movie");

        var notChosenContext = SeedProfileContext(notChosen);
        var notChosenService = CreateService(notChosenContext, RealReaders());
        Assert.Equal([standard.WorkId], (await notChosenService.LoadWorksAsync(default)).Select(row => row.WorkId).ToArray());
        Assert.Equal([standard.WorkId], (await notChosenService.LoadHomeWorksAsync(default)).Select(row => row.WorkId).ToArray());
        Assert.Equal([standard.WorkId], (await notChosenService.LoadVisibleWorkIdsAsync(default)).ToArray());
        Assert.Equal([standard.WorkId], (await notChosenService.LoadWorkLibraryRowsAsync(default)).Select(row => row.WorkId).ToArray());
        Assert.False((await notChosenService.LoadLibraryScopeAsync(default)).Allows(selectedLibrary));
        Assert.True((await notChosenService.LoadLibraryScopeAsync(default)).Allows(standardLibrary));
        Assert.False((await notChosenService.LoadLibraryAccessAsync(default)).Allows(selectedLibrary.ToString("D"), "Movie"));
        Assert.DoesNotContain(selected.WorkId, (await notChosenService.LoadDetailWorksAsync(selected.WorkId, default)).Select(row => row.WorkId));
        foreach (var permission in new[] { ApplicationPermissionIds.PlaybackRead, ApplicationPermissionIds.ArtworkRead })
        {
            Assert.Equal(CatalogueResourceAccess.Denied,
                await CreateResourceService(notChosenContext).EvaluateAssetAsync(notChosenContext, selected.AssetId, permission));
            Assert.Equal(CatalogueResourceAccess.Allowed,
                await CreateResourceService(notChosenContext).EvaluateAssetAsync(notChosenContext, standard.AssetId, permission));
        }

        var chosenContext = SeedProfileContext(chosen);
        var chosenService = CreateService(chosenContext, RealReaders());
        Assert.Equal(new[] { standard.WorkId, selected.WorkId }.Order(), (await chosenService.LoadWorksAsync(default)).Select(row => row.WorkId).Order());
        Assert.Equal(CatalogueResourceAccess.Allowed,
            await CreateResourceService(chosenContext).EvaluateAssetAsync(chosenContext, selected.AssetId, ApplicationPermissionIds.PlaybackRead));

        var administratorService = CreateService(SeedProfileContext(administrator), RealReaders());
        Assert.Equal(new[] { standard.WorkId, selected.WorkId }.Order(), (await administratorService.LoadWorksAsync(default)).Select(row => row.WorkId).Order());
    }

    [Fact]
    public async Task ASelectedLibraryWithNobodyChosen_IsSeenByAdministratorsOnly()
    {
        var selectedLibrary = Guid.NewGuid();
        _selectedLibraries.Selected.Add(selectedLibrary);
        var person = Guid.NewGuid();
        var administrator = Guid.NewGuid();
        await CreateHumanAsync(person, WatchOnly, new HashSet<Guid>());
        await CreateHumanAsync(administrator, EveryFeature, new HashSet<Guid>(), administrator: true);
        var selected = await InsertOwnedWorkWithIdAsync(selectedLibrary, "Admins only", "Movie");

        Assert.Empty(await CreateService(SeedProfileContext(person), RealReaders()).LoadWorksAsync(default));
        Assert.Equal([selected.WorkId],
            (await CreateService(SeedProfileContext(administrator), RealReaders()).LoadWorksAsync(default)).Select(row => row.WorkId).ToArray());
    }

    [Fact]
    public async Task ANewProfile_StartsWithoutAccessToAnExistingSelectedLibrary()
    {
        var selectedLibrary = Guid.NewGuid();
        _selectedLibraries.Selected.Add(selectedLibrary);
        var earlier = Guid.NewGuid();
        await CreateHumanAsync(earlier, WatchOnly, new HashSet<Guid>());
        await GrantAsync(selectedLibrary, ProfileOf(earlier));
        var selected = await InsertOwnedWorkWithIdAsync(selectedLibrary, "Selected film", "Movie");
        Assert.Equal([selected.WorkId],
            (await CreateService(SeedProfileContext(earlier), RealReaders()).LoadWorksAsync(default)).Select(row => row.WorkId).ToArray());

        var later = Guid.NewGuid();
        await CreateHumanAsync(later, WatchOnly, new HashSet<Guid>());

        Assert.Empty(await CreateService(SeedProfileContext(later), RealReaders()).LoadWorksAsync(default));
        using var connection = _database.CreateConnection();
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM library_profile_grants WHERE profile_id=@id;", new { id = ProfileOf(later) }));
    }

    [Fact]
    public async Task SwitchingALibraryBackToStandard_ShowsItAgainToEveryoneWithTheArea()
    {
        var library = Guid.NewGuid();
        _selectedLibraries.Selected.Add(library);
        var person = Guid.NewGuid();
        await CreateHumanAsync(person, WatchOnly, new HashSet<Guid>());
        var film = await InsertOwnedWorkWithIdAsync(library, "A film", "Movie");
        Assert.Empty(await CreateService(SeedProfileContext(person), RealReaders()).LoadWorksAsync(default));

        _selectedLibraries.Selected.Remove(library);

        Assert.Equal([film.WorkId],
            (await CreateService(SeedProfileContext(person), RealReaders()).LoadWorksAsync(default)).Select(row => row.WorkId).ToArray());
    }

    [Fact]
    public async Task ASelectedLibrary_StillFollowsTheProfilesOtherLimits_EvenWhenChosen()
    {
        var selectedLibrary = Guid.NewGuid();
        _selectedLibraries.Selected.Add(selectedLibrary);
        var person = Guid.NewGuid();
        // Chosen for the library, but without the Watch area and with the library also hidden: the older rules still apply.
        await CreateHumanAsync(person, new HashSet<AccountFeatureId> { AccountFeatureId.Read }, new HashSet<Guid> { selectedLibrary });
        await GrantAsync(selectedLibrary, ProfileOf(person));
        await InsertOwnedWorkWithIdAsync(selectedLibrary, "A film", "Movie");

        Assert.Empty(await CreateService(SeedProfileContext(person), RealReaders()).LoadWorksAsync(default));
    }
}
