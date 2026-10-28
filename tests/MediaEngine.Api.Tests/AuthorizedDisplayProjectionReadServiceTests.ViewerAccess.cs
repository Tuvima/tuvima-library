using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Domain.Authorization;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Http;

namespace MediaEngine.Api.Tests;

// Vault sections spec D27, D32 (WP0), security review leak 2: people counts and the people reads go through the one access service, so
// a title the viewer cannot see (Vault, hidden library, over the content limit) adds nothing to a count and names nobody.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private ViewerAccessService ViewerAccessFor(DefaultHttpContext context) => new(CreateService(context, RealReaders()));

    private PersonPresenceReadService PresenceReader() => new(new PersonRepository(_database));

    private PersonAssetScopeReadService PeopleScope() => new(new PersonRepository(_database), _database);

    /// <summary>Credits a person on a work the way the library does (the canonical cast list) and links them to its file.</summary>
    private Guid CreditPerson(string name, string? qid, params (Guid WorkId, Guid AssetId)[] films)
    {
        var person = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO persons(id, name, wikidata_qid, created_at) VALUES(@person, @name, @qid, datetime('now'));",
            new { person, name, qid });
        foreach (var film in films)
        {
            connection.Execute(
                """
                INSERT INTO canonical_value_arrays(entity_id, key, ordinal, value, value_qid)
                SELECT @work, 'cast_member', COALESCE(MAX(ordinal) + 1, 0), @name, @qid FROM canonical_value_arrays WHERE entity_id = @work AND key = 'cast_member';
                INSERT INTO person_media_links(media_asset_id, person_id, role) VALUES(@asset, @person, 'Actor');
                """,
                new { work = film.WorkId, asset = film.AssetId, person, name, qid });
        }

        return person;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ViewerAccess_PeopleCounts_NeverIncludeTheVault_LockedOrUnlocked(bool unlocked)
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var shared = CreditPerson("Shared Performer", "QSHARED", shape.Normal, shape.Secret);
        var vaultOnly = CreditPerson("Vault Only Performer", "QVAULTONLY", shape.Secret);
        var session = NewVaultSession(shape.Account);
        if (unlocked)
        {
            await UnlockVaultAsync(session);
        }

        var viewer = await ViewerAccessFor(VaultContext(shape.Account, session)).GetCatalogueAsync();
        var presence = await PresenceReader().GetPresenceAsync([shared, vaultOnly], viewer, default);

        // The shared person counts the ordinary film only; the person seen only in the Vault is not there at all, so no count names them.
        Assert.Equal(1, presence[shared.ToString()]["Movie"]);
        Assert.DoesNotContain(vaultOnly.ToString(), presence.Keys);
        Assert.False(viewer.CanSeeAsset(shape.Secret.AssetId));
        Assert.True(viewer.CanSeeAsset(shape.Normal.AssetId));
    }

    [Fact]
    public async Task ViewerAccess_PeopleReads_NameNoOneOnlyFoundInTheVault()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var shared = CreditPerson("Shared Performer", "QSHARED", shape.Normal, shape.Secret);
        var vaultOnly = CreditPerson("Vault Only Performer", "QVAULTONLY", shape.Secret);
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var viewer = await ViewerAccessFor(VaultContext(shape.Account, session)).GetCatalogueAsync();
        var people = PeopleScope();

        var everyone = await people.GetCanonicalPersonIdsAsync(viewer, default);
        Assert.Contains(shared, everyone);
        Assert.DoesNotContain(vaultOnly, everyone);
        Assert.Empty(await people.GetByWorkAsync(shape.Secret.WorkId, viewer, default));
        Assert.Contains(shared, (await people.GetByWorkAsync(shape.Normal.WorkId, viewer, default)).Select(person => person.Id));
        var roles = await people.GetCanonicalRoleCountsAsync(viewer, default);
        Assert.Equal(1, roles.Values.Sum());
    }

    [Fact]
    public async Task ViewerAccess_AHiddenLibrary_AddsNothingToAProfilesPeopleCounts_ButStillCountsForAnotherProfile()
    {
        var hiddenLibrary = Guid.NewGuid();
        var shownLibrary = Guid.NewGuid();
        var sam = Guid.NewGuid();
        await CreateHumanAsync(sam, WatchOnly, new HashSet<Guid> { hiddenLibrary });
        var shown = await InsertOwnedWorkWithIdAsync(shownLibrary, "Shown film", "Movie");
        var secret = await InsertOwnedWorkWithIdAsync(hiddenLibrary, "Hidden film", "Movie");
        var person = CreditPerson("Shared Performer", "QSHARED", shown, secret);
        var onlyHidden = CreditPerson("Hidden Library Performer", "QHIDDEN", secret);

        var samViewer = await ViewerAccessFor(SeedProfileContext(sam)).GetCatalogueAsync();
        var samPresence = await PresenceReader().GetPresenceAsync([person, onlyHidden], samViewer, default);

        Assert.Equal(1, samPresence[person.ToString()]["Movie"]);
        Assert.DoesNotContain(onlyHidden.ToString(), samPresence.Keys);

        // The same data for a profile the library is not hidden from counts both films.
        using var connection = _database.CreateConnection();
        connection.Execute("DELETE FROM profile_library_hidden;");
        var openViewer = await ViewerAccessFor(SeedProfileContext(sam)).GetCatalogueAsync();
        var openPresence = await PresenceReader().GetPresenceAsync([person, onlyHidden], openViewer, default);
        Assert.Equal(2, openPresence[person.ToString()]["Movie"]);
        Assert.Equal(1, openPresence[onlyHidden.ToString()]["Movie"]);
    }

    [Fact]
    public async Task ViewerAccess_TheContentLimit_AddsNothingToAProfilesPeopleCounts()
    {
        var account = Guid.NewGuid();
        var library = Guid.NewGuid();
        await CreateHumanAsync(account, WatchOnly, new HashSet<Guid>());
        var family = await InsertOwnedWorkWithIdAsync(library, "Family film", "Movie");
        var adult = await InsertOwnedWorkWithIdAsync(library, "Adult film", "Movie");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                """
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@family, 'content_rating', 'PG', CURRENT_TIMESTAMP), (@adult, 'content_rating', 'R', CURRENT_TIMESTAMP);
                """,
                new { family = family.WorkId, adult = adult.WorkId });
        }

        var person = CreditPerson("Shared Performer", "QSHARED", family, adult);
        var onlyAdult = CreditPerson("Adult Only Performer", "QADULT", adult);

        SetContentLimit("PG");
        var limited = await ViewerAccessFor(SeedProfileContext(account)).GetCatalogueAsync();
        var limitedPresence = await PresenceReader().GetPresenceAsync([person, onlyAdult], limited, default);
        SetContentLimit("R");
        var raised = await ViewerAccessFor(SeedProfileContext(account)).GetCatalogueAsync();
        var raisedPresence = await PresenceReader().GetPresenceAsync([person, onlyAdult], raised, default);

        Assert.Equal(1, limitedPresence[person.ToString()]["Movie"]);
        Assert.DoesNotContain(onlyAdult.ToString(), limitedPresence.Keys);
        Assert.Equal(2, raisedPresence[person.ToString()]["Movie"]);
        Assert.Equal(1, raisedPresence[onlyAdult.ToString()]["Movie"]);
    }

    [Fact]
    public async Task ViewerAccess_ForAViewerWhoCanSeeEverything_GivesTheSameCountsAsTheUnfilteredRead()
    {
        // Non-Vault viewers see results identical to today.
        var account = Guid.NewGuid();
        var library = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid>());
        var first = await InsertOwnedWorkWithIdAsync(library, "First film", "Movie");
        var second = await InsertOwnedWorkWithIdAsync(library, "Second film", "Movie");
        var person = CreditPerson("Shared Performer", "QSHARED", first, second);

        var viewer = await ViewerAccessFor(SeedProfileContext(account)).GetCatalogueAsync();
        var filtered = await PresenceReader().GetPresenceAsync([person], viewer, default);
        var unfiltered = await new PersonRepository(_database).GetPresenceBatchAsync([person]);

        Assert.Equal(unfiltered[person], filtered[person.ToString()]);
        Assert.Equal(2, filtered[person.ToString()]["Movie"]);
    }

    [Fact]
    public async Task ViewerAccess_ARequestWithNoSignedInPerson_SeesNothing()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var person = CreditPerson("Shared Performer", "QSHARED", shape.Normal);

        var viewer = await ViewerAccessFor(new DefaultHttpContext()).GetCatalogueAsync();
        var presence = await PresenceReader().GetPresenceAsync([person], viewer, default);

        Assert.Empty(viewer.WorkIds);
        Assert.Empty(presence);
    }

    [Fact]
    public async Task ViewerAccess_TheVisibleWorks_AreTheSameListTheSharedListsAreBuiltFrom()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var context = VaultContext(shape.Account, NewVaultSession(shape.Account));
        var authorized = CreateService(context, RealReaders());
        var access = new ViewerAccessService(authorized);

        Assert.Equal(
            (await authorized.LoadWorksAsync(default)).Select(row => row.WorkId).Order(),
            (await access.GetVisibleWorksAsync()).Select(row => row.WorkId).Order());
        Assert.Equal(await authorized.LoadVisibleWorkIdsAsync(default), await access.GetVisibleWorkIdsAsync());
        Assert.Equal([shape.Normal.WorkId], (await access.GetCatalogueAsync()).WorkIds.ToArray());
    }
}
