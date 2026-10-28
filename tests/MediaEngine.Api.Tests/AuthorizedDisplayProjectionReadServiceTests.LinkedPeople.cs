using System.Text.Json;
using Dapper;
using MediaEngine.Api.Services.Details.Internals;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Authorization;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

// Security review of Vault sections WP0, finding 1: a person reached through a link (a pen name behind a real name, a group's members,
// the groups a person belongs to) is shown only when the viewer could open that person. A pen name visible on an ordinary book must not
// expose a real person who is credited only on Vault, hidden-library or over-limit titles. Each people route is called as a locked
// viewer, an unlocked one and an ordinary profile whose results must be unchanged.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private PersonAliasReadService AliasReader() => new(new PersonRepository(_database), _database);

    private PersonCreditReadService CreditReader() =>
        new(new CanonicalValueArrayRepository(_database), new PersonRepository(_database), _database);

    private Guid PersonWithoutCredits(string name, bool isGroup = false, bool isPseudonym = false)
    {
        var person = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO persons(id, name, is_group, is_pseudonym, created_at) VALUES(@person, @name, @isGroup, @isPseudonym, datetime('now'));",
            new { person, name, isGroup = isGroup ? 1 : 0, isPseudonym = isPseudonym ? 1 : 0 });
        return person;
    }

    private void MarkPenName(Guid person)
    {
        using var connection = _database.CreateConnection();
        connection.Execute("UPDATE persons SET is_pseudonym = 1 WHERE id = @person;", new { person });
    }

    private void LinkAlias(Guid pseudonym, Guid real)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO person_aliases(pseudonym_person_id, real_person_id) VALUES(@pseudonym, @real);",
            new { pseudonym, real });
    }

    private void LinkMember(Guid group, Guid member)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO person_group_members(group_id, member_id) VALUES(@group, @member);",
            new { group, member });
    }

    private void MarkGroup(Guid group)
    {
        using var connection = _database.CreateConnection();
        connection.Execute("UPDATE persons SET is_group = 1 WHERE id = @group;", new { group });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedPeople_APenNameOnAnOrdinaryBook_NeverExposesARealPersonKnownOnlyFromTheVault(bool unlocked)
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var pen = CreditPerson("Pen Name", "QPEN", shape.Normal);
        MarkPenName(pen);
        var vaultReal = CreditPerson("Real Vault Name", "QVAULTREAL", shape.Secret);
        var ordinaryReal = CreditPerson("Real Ordinary Name", "QORDINARYREAL", shape.Normal);
        var noCredits = PersonWithoutCredits("Real Without Credits");
        LinkAlias(pen, vaultReal);
        LinkAlias(pen, ordinaryReal);
        LinkAlias(pen, noCredits);
        var session = NewVaultSession(shape.Account);
        if (unlocked)
        {
            await UnlockVaultAsync(session);
        }

        var viewer = await ViewerAccessFor(VaultContext(shape.Account, session)).GetCatalogueAsync();
        var response = await AliasReader().GetAliasesAsync(pen, viewer, default);

        Assert.NotNull(response);
        // The ordinary real person and the person with no credits at all are shown exactly as before; the Vault-only one is not there.
        Assert.Equal(["Real Ordinary Name", "Real Without Credits"], response!.Aliases.Select(alias => alias.Name).Order().ToArray());
        var wire = JsonSerializer.Serialize(response);
        Assert.DoesNotContain("Real Vault Name", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("QVAULTREAL", wire, StringComparison.Ordinal);
        Assert.DoesNotContain(vaultReal.ToString(), wire, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LinkedPeople_TheRealPersonSideOfAnAliasLink_IsFilteredTheSameWay()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var real = CreditPerson("Real Ordinary Name", "QORDINARYREAL", shape.Normal);
        var vaultPen = CreditPerson("Vault Pen Name", "QVAULTPEN", shape.Secret);
        MarkPenName(vaultPen);
        LinkAlias(vaultPen, real);
        var viewer = await ViewerAccessFor(VaultContext(shape.Account, NewVaultSession(shape.Account))).GetCatalogueAsync();

        var response = await AliasReader().GetAliasesAsync(real, viewer, default);

        Assert.NotNull(response);
        Assert.Empty(response!.Aliases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedPeople_GroupMembersAndMemberships_NeverNameAPersonKnownOnlyFromTheVault(bool unlocked)
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var group = CreditPerson("Ordinary Band", "QBAND", shape.Normal);
        MarkGroup(group);
        var ordinaryMember = CreditPerson("Ordinary Member", "QMEMBER", shape.Normal);
        var vaultMember = CreditPerson("Vault Only Member", "QVAULTMEMBER", shape.Secret);
        var noCreditMember = PersonWithoutCredits("Member Without Credits");
        LinkMember(group, ordinaryMember);
        LinkMember(group, vaultMember);
        LinkMember(group, noCreditMember);
        var vaultGroup = CreditPerson("Vault Only Band", "QVAULTBAND", shape.Secret);
        MarkGroup(vaultGroup);
        LinkMember(vaultGroup, ordinaryMember);
        var session = NewVaultSession(shape.Account);
        if (unlocked)
        {
            await UnlockVaultAsync(session);
        }

        var viewer = await ViewerAccessFor(VaultContext(shape.Account, session)).GetCatalogueAsync();
        var members = await CreditReader().GetGroupMembersAsync(group, isGroup: true, viewer, default);
        var memberships = await CreditReader().GetGroupMembersAsync(ordinaryMember, isGroup: false, viewer, default);

        Assert.Equal(["Member Without Credits", "Ordinary Member"], members.Select(member => member.Name).ToArray());
        // The member also belongs to a band known only from the Vault; only the ordinary band is named.
        Assert.Equal(["Ordinary Band"], memberships.Select(group => group.Name).ToArray());
        Assert.DoesNotContain("Vault Only", JsonSerializer.Serialize(members) + JsonSerializer.Serialize(memberships), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinkedPeople_AHiddenLibrary_HidesTheLinkedPersonOnlyFromTheProfileItIsHiddenFrom()
    {
        var hiddenLibrary = Guid.NewGuid();
        var shownLibrary = Guid.NewGuid();
        var sam = Guid.NewGuid();
        await CreateHumanAsync(sam, WatchOnly, new HashSet<Guid> { hiddenLibrary });
        var shown = await InsertOwnedWorkWithIdAsync(shownLibrary, "Shown film", "Movie");
        var hidden = await InsertOwnedWorkWithIdAsync(hiddenLibrary, "Hidden film", "Movie");
        var pen = CreditPerson("Pen Name", "QPEN", shown);
        MarkPenName(pen);
        var hiddenReal = CreditPerson("Hidden Library Real", "QHIDDENREAL", hidden);
        LinkAlias(pen, hiddenReal);

        var samViewer = await ViewerAccessFor(SeedProfileContext(sam)).GetCatalogueAsync();
        Assert.Empty((await AliasReader().GetAliasesAsync(pen, samViewer, default))!.Aliases);

        // The same data for a profile the library is not hidden from shows the linked person, as before.
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("DELETE FROM profile_library_hidden;");
        }

        var openViewer = await ViewerAccessFor(SeedProfileContext(sam)).GetCatalogueAsync();
        Assert.Equal("Hidden Library Real", Assert.Single((await AliasReader().GetAliasesAsync(pen, openViewer, default))!.Aliases).Name);
    }

    [Fact]
    public async Task LinkedPeople_TheContentLimit_HidesALinkedPersonWhoseOnlyTitlesAreOverTheLimit()
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

        var pen = CreditPerson("Pen Name", "QPEN", family);
        MarkPenName(pen);
        var adultReal = CreditPerson("Adult Only Real", "QADULTREAL", adult);
        LinkAlias(pen, adultReal);

        SetContentLimit("PG");
        var limited = await ViewerAccessFor(SeedProfileContext(account)).GetCatalogueAsync();
        Assert.Empty((await AliasReader().GetAliasesAsync(pen, limited, default))!.Aliases);

        SetContentLimit("R");
        var raised = await ViewerAccessFor(SeedProfileContext(account)).GetCatalogueAsync();
        Assert.Single((await AliasReader().GetAliasesAsync(pen, raised, default))!.Aliases);
    }

    [Fact]
    public async Task LinkedPeople_ThePersonDetailPage_NamesNoLinkedPersonTheViewerCannotOpen()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var pen = CreditPerson("Pen Name", "QPEN", shape.Normal);
        MarkPenName(pen);
        var vaultReal = CreditPerson("Real Vault Name", "QVAULTREAL", shape.Secret);
        var ordinaryReal = CreditPerson("Real Ordinary Name", "QORDINARYREAL", shape.Normal);
        LinkAlias(pen, vaultReal);
        LinkAlias(pen, ordinaryReal);
        var band = CreditPerson("Ordinary Band", "QBAND", shape.Normal);
        MarkGroup(band);
        var vaultMember = CreditPerson("Vault Only Member", "QVAULTMEMBER", shape.Secret);
        LinkMember(band, vaultMember);
        var viewer = await ViewerAccessFor(VaultContext(shape.Account, NewVaultSession(shape.Account))).GetCatalogueAsync();

        var penPage = await ComposePersonAsync(pen, viewer);
        var bandPage = await ComposePersonAsync(band, viewer);
        var penPageForNobody = await ComposePersonAsync(pen, viewerCatalogue: null);

        Assert.NotNull(penPage);
        Assert.NotNull(bandPage);
        var penWire = JsonSerializer.Serialize(penPage);
        var bandWire = JsonSerializer.Serialize(bandPage);
        Assert.Contains("Real Ordinary Name", penWire, StringComparison.Ordinal);
        Assert.DoesNotContain("Real Vault Name", penWire, StringComparison.Ordinal);
        Assert.DoesNotContain("QVAULTREAL", penWire, StringComparison.Ordinal);
        Assert.DoesNotContain("Vault Only Member", bandWire, StringComparison.Ordinal);
        // A caller that supplies no viewer sees no credited linked person at all: it fails closed.
        Assert.DoesNotContain("Real Vault Name", JsonSerializer.Serialize(penPageForNobody), StringComparison.Ordinal);
        Assert.DoesNotContain("Real Ordinary Name", JsonSerializer.Serialize(penPageForNobody), StringComparison.Ordinal);
    }

    private Task<MediaEngine.Contracts.Details.DetailPageViewModel?> ComposePersonAsync(Guid person, ViewerCatalogue? viewerCatalogue) =>
        CreateComposer().BuildAuthorizedAsync(
            DetailEntityType.Person,
            person,
            DetailPresentationContext.Default,
            CancellationToken.None,
            selectedContainerId: null,
            MediaEngine.Domain.Aggregates.Profile.SeedProfileId,
            default(DetailActionAuthorizationContext),
            authorizedAssetIds: null,
            authorizedWorks: null,
            viewerCatalogue: viewerCatalogue);

    [Fact]
    public async Task LinkedPeople_EveryPeopleRead_ForALockedViewer_NamesNoOneKnownOnlyFromTheVault_AndANonVaultViewerSeesWhatItDid()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var collection = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("INSERT INTO collections (id, created_at) VALUES (@collection, datetime('now'));", new { collection });
            connection.Execute(
                "UPDATE works SET collection_id = @collection WHERE id IN (@normal, @secret);",
                new { collection, normal = shape.Normal.WorkId, secret = shape.Secret.WorkId });
        }

        var shared = CreditPerson("Shared Performer", "QSHARED", shape.Normal, shape.Secret);
        var vaultOnly = CreditPerson("Vault Only Performer", "QVAULTONLY", shape.Secret);
        var viewer = await ViewerAccessFor(VaultContext(shape.Account, NewVaultSession(shape.Account))).GetCatalogueAsync();
        var people = PeopleScope();

        // /persons, /persons/role-counts and /persons/presence
        var everyone = await people.GetCanonicalPersonIdsAsync(viewer, default);
        Assert.Contains(shared, everyone);
        Assert.DoesNotContain(vaultOnly, everyone);
        var presence = await PresenceReader().GetPresenceAsync([shared, vaultOnly], viewer, default);
        Assert.Equal(1, presence[shared.ToString()]["Movie"]);
        Assert.DoesNotContain(vaultOnly.ToString(), presence.Keys);
        Assert.Equal(1, (await people.GetCanonicalRoleCountsAsync(viewer, default)).Values.Sum());

        // /persons/by-collection/{id} and /persons/by-work/{id}
        var byCollection = await people.GetByCollectionAsync(collection, viewer, default);
        Assert.Equal([shared], byCollection.Select(person => person.Id).ToArray());
        Assert.Empty(await people.GetByWorkAsync(shape.Secret.WorkId, viewer, default));
        Assert.Equal([shared], (await people.GetByWorkAsync(shape.Normal.WorkId, viewer, default)).Select(person => person.Id).ToArray());

        // /persons/{id}/library-credits and /persons/{id}/works: the Vault film is not among the works the viewer may see.
        var access = ViewerAccessFor(VaultContext(shape.Account, NewVaultSession(shape.Account)));
        var visibleWorks = await access.GetVisibleWorkIdsAsync();
        Assert.Contains(shape.Normal.WorkId, visibleWorks);
        Assert.DoesNotContain(shape.Secret.WorkId, visibleWorks);
        var credits = await CreditReader().GetLibraryCreditsAsync(shared, default);
        var allowedCredits = PersonLibraryCreditAuthorizationPolicy.Filter(credits, await access.GetVisibleWorksAsync());
        Assert.DoesNotContain(allowedCredits, credit => credit.WorkId == shape.Secret.WorkId);
    }

    [Fact]
    public async Task LinkedPeople_AnAudiobookNarratorCredit_OnAnotherFileOfAVisibleWork_StillCounts()
    {
        // The catalogue holds one chosen file per (work, library); a credit on another file of the same visible work (a narrator credited
        // only on the audiobook edition while the list shows the book) must keep counting for an ordinary viewer (finding 3).
        var account = Guid.NewGuid();
        var library = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid>());
        var book = await InsertOwnedWorkWithIdAsync(library, "Book with narration", "Book");
        var audiobookAsset = Guid.NewGuid();
        var audiobookEdition = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                """
                INSERT INTO editions (id, work_id) VALUES (@edition, @work);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id)
                VALUES (@asset, @edition, @hash, @path, datetime('now', '+1 day'), @library);
                """,
                new
                {
                    edition = audiobookEdition,
                    work = book.WorkId,
                    asset = audiobookAsset,
                    hash = Guid.NewGuid().ToString("N"),
                    path = $"C:/library/{audiobookAsset:N}.m4b",
                    library = library.ToString("D"),
                });
        }

        // The narrator is credited on the audiobook file itself, not on the work, so the book file does not credit them.
        var narrator = CreditPerson("Only Narrator", "QNARRATOR");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                """
                INSERT INTO canonical_value_arrays(entity_id, key, ordinal, value, value_qid) VALUES(@asset, 'cast_member', 0, 'Only Narrator', 'QNARRATOR');
                INSERT INTO person_media_links(media_asset_id, person_id, role) VALUES(@asset, @person, 'Narrator');
                """,
                new { asset = audiobookAsset, person = narrator });
        }

        var viewer = await ViewerAccessFor(SeedProfileContext(account)).GetCatalogueAsync();
        var presence = await PresenceReader().GetPresenceAsync([narrator], viewer, default);

        Assert.Equal(1, presence[narrator.ToString()]["Book"]);

        // Even when the viewer's catalogue holds only the file the list chose (the book), the narrator's credit on the other file of the
        // same visible work in the same library counts; the same credit does not count in a library the viewer cannot see.
        var chosenOnly = new ViewerCatalogue([new ViewerCatalogueFile(book.WorkId, book.AssetId, DateTimeOffset.UtcNow, library.ToString("D").ToUpperInvariant())]);
        Assert.False(chosenOnly.CanSeeAsset(audiobookAsset));
        var counted = await PresenceReader().GetPresenceAsync([narrator], chosenOnly, default);
        Assert.Equal(1, counted[narrator.ToString()]["Book"]);
        var otherLibrary = new ViewerCatalogue([new ViewerCatalogueFile(book.WorkId, book.AssetId, DateTimeOffset.UtcNow, Guid.NewGuid().ToString("D"))]);
        Assert.Empty(await PresenceReader().GetPresenceAsync([narrator], otherLibrary, default));
    }
}
