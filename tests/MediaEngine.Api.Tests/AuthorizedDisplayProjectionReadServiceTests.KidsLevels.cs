using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;

namespace MediaEngine.Api.Tests;

// Library access D9, D10: a Kid's profile follows its household's level for the item's area (Watch, Read, Listen) and one "Show unrated items"
// switch. These run the real read service and resource checks on a temp data store, so lists, counts and playback agree.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private async Task<(Guid Account, Guid Profile, Guid Library, KidsCatalogue Items)> SeedKidsCatalogueAsync()
    {
        var account = Guid.NewGuid();
        var library = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid>());
        var items = new KidsCatalogue(
            await InsertOwnedWorkWithIdAsync(library, "Family film", "Movie"),
            await InsertOwnedWorkWithIdAsync(library, "Mature film", "Movie"),
            await InsertOwnedWorkWithIdAsync(library, "Home video", "Movie"),
            await InsertOwnedWorkWithIdAsync(library, "Picture book", "Book"),
            await InsertOwnedWorkWithIdAsync(library, "Teen novel", "Book"),
            await InsertOwnedWorkWithIdAsync(library, "Unrated novel", "Book"),
            await InsertOwnedWorkWithIdAsync(library, "Lullabies", "Music"),
            await InsertOwnedWorkWithIdAsync(library, "Explicit album", "Music"),
            await InsertOwnedWorkWithIdAsync(library, "Mixtape", "Music"));
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                """
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@familyFilm, 'content_rating', 'PG', CURRENT_TIMESTAMP),
                       (@matureFilm, 'content_rating', 'R', CURRENT_TIMESTAMP),
                       (@pictureBook, 'content_rating', 'G', CURRENT_TIMESTAMP),
                       (@teenNovel, 'content_rating', 'PG-13', CURRENT_TIMESTAMP),
                       (@lullabies, 'content_rating', 'PG', CURRENT_TIMESTAMP),
                       (@explicitAlbum, 'content_rating', 'R', CURRENT_TIMESTAMP);
                """,
                new
                {
                    familyFilm = items.FamilyFilm.WorkId,
                    matureFilm = items.MatureFilm.WorkId,
                    pictureBook = items.PictureBook.WorkId,
                    teenNovel = items.TeenNovel.WorkId,
                    lullabies = items.Lullabies.WorkId,
                    explicitAlbum = items.ExplicitAlbum.WorkId,
                });
        }

        return (account, MediaEngine.Domain.Aggregates.Profile.SeedProfileId, library, items);
    }

    private sealed record KidsCatalogue(
        (Guid WorkId, Guid AssetId) FamilyFilm,
        (Guid WorkId, Guid AssetId) MatureFilm,
        (Guid WorkId, Guid AssetId) HomeVideo,
        (Guid WorkId, Guid AssetId) PictureBook,
        (Guid WorkId, Guid AssetId) TeenNovel,
        (Guid WorkId, Guid AssetId) UnratedNovel,
        (Guid WorkId, Guid AssetId) Lullabies,
        (Guid WorkId, Guid AssetId) ExplicitAlbum,
        (Guid WorkId, Guid AssetId) Mixtape);

    [Fact]
    public async Task KidsLevels_AKidsProfileAtPg_SeesEveryUnratedItemWithTheSwitchOn_AndNoneWithItOff_WhileARatedRFilmStaysHiddenEitherWay()
    {
        var (account, profile, _, items) = await SeedKidsCatalogueAsync();
        var context = HumanContext(account, profile);
        KidsLimitTestSupport.SetLimit(_database, "PG", showUnrated: true);

        var withSwitchOn = (await CreateService(context, new StubRawProjection([], [])).LoadAuthorizedAssetsAsync(CancellationToken.None))
            .Select(row => row.AssetId).ToHashSet();

        Assert.Equal(
            new[] { items.FamilyFilm, items.HomeVideo, items.PictureBook, items.UnratedNovel, items.Lullabies, items.Mixtape }.Select(item => item.AssetId).Order(),
            withSwitchOn.Order());
        Assert.DoesNotContain(items.MatureFilm.AssetId, withSwitchOn);

        KidsLimitTestSupport.SetLimit(_database, "PG", showUnrated: false);

        var withSwitchOff = (await CreateService(context, new StubRawProjection([], [])).LoadAuthorizedAssetsAsync(CancellationToken.None))
            .Select(row => row.AssetId).ToHashSet();

        Assert.Equal(
            new[] { items.FamilyFilm, items.PictureBook, items.Lullabies }.Select(item => item.AssetId).Order(),
            withSwitchOff.Order());
        Assert.DoesNotContain(items.MatureFilm.AssetId, withSwitchOff);
    }

    [Fact]
    public async Task KidsLevels_WatchReadAndListenFollowTheirOwnLevels_InListsAndOnPlayback()
    {
        var (account, profile, _, items) = await SeedKidsCatalogueAsync();
        var context = HumanContext(account, profile);

        // Watch G (the PG film is out), Read PG-13 (the teen novel is in), Listen R (the explicit album is in).
        KidsLimitTestSupport.SetLimit(_database, profile, watch: "G", read: "PG-13", listen: "R", showUnrated: false);

        var listed = (await CreateService(context, new StubRawProjection([], [])).LoadAuthorizedAssetsAsync(CancellationToken.None))
            .Select(row => row.AssetId).ToHashSet();
        var resources = CreateResourceService(context);

        Assert.Equal(
            new[] { items.PictureBook, items.TeenNovel, items.Lullabies, items.ExplicitAlbum }.Select(item => item.AssetId).Order(),
            listed.Order());
        foreach (var (item, allowed) in new[]
        {
            (items.FamilyFilm, false), (items.MatureFilm, false), (items.HomeVideo, false),
            (items.PictureBook, true), (items.TeenNovel, true), (items.UnratedNovel, false),
            (items.Lullabies, true), (items.ExplicitAlbum, true), (items.Mixtape, false),
        })
        {
            // Playback and the file route agree with the list, item by item.
            Assert.Equal(allowed, listed.Contains(item.AssetId));
            Assert.Equal(
                allowed ? CatalogueResourceAccess.Allowed : CatalogueResourceAccess.Denied,
                await resources.EvaluateAssetAsync(context, item.AssetId, ApplicationPermissionIds.PlaybackRead));
        }
    }

    [Fact]
    public async Task KidsLevels_AChangeToTheHouseholdLevels_AppliesOnTheVeryNextRead_WithNoCopyOnTheProfile()
    {
        var (account, profile, _, items) = await SeedKidsCatalogueAsync();
        var context = HumanContext(account, profile);
        KidsLimitTestSupport.SetLimit(_database, "PG", showUnrated: true);
        Assert.Contains(items.HomeVideo.AssetId,
            (await CreateService(context, new StubRawProjection([], [])).LoadAuthorizedAssetsAsync(CancellationToken.None)).Select(row => row.AssetId));

        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "UPDATE households SET kid_watch_level='R', kid_show_unrated=0 WHERE id=(SELECT household_id FROM profiles WHERE id=@profile);",
                new { profile });
        }

        var after = (await CreateService(context, new StubRawProjection([], [])).LoadAuthorizedAssetsAsync(CancellationToken.None))
            .Select(row => row.AssetId).ToHashSet();
        Assert.Contains(items.MatureFilm.AssetId, after);
        Assert.DoesNotContain(items.HomeVideo.AssetId, after);
    }

    [Fact]
    public async Task KidsLevels_APeopleCountMatchesTheListForTheSameKidsProfile()
    {
        var (account, profile, _, items) = await SeedKidsCatalogueAsync();
        var person = CreditPerson("Shared Performer", "QSHARED", items.FamilyFilm, items.MatureFilm, items.PictureBook, items.TeenNovel);
        KidsLimitTestSupport.SetLimit(_database, profile, watch: "PG", read: "G", listen: "PG", showUnrated: true);

        var viewer = await ViewerAccessFor(SeedProfileContext(account)).GetCatalogueAsync();
        var presence = await PresenceReader().GetPresenceAsync([person], viewer, default);

        // One film (the R film is over the Watch level) and one book (the PG-13 novel is over the Read level).
        Assert.Equal(1, presence[person.ToString()]["Movie"]);
        Assert.Equal(1, presence[person.ToString()]["Book"]);

        KidsLimitTestSupport.SetLimit(_database, profile, watch: "R", read: "PG-13", listen: "PG", showUnrated: true);
        var raised = await ViewerAccessFor(SeedProfileContext(account)).GetCatalogueAsync();
        var raisedPresence = await PresenceReader().GetPresenceAsync([person], raised, default);
        Assert.Equal(2, raisedPresence[person.ToString()]["Movie"]);
        Assert.Equal(2, raisedPresence[person.ToString()]["Book"]);
    }

    [Fact]
    public async Task KidsLevels_AProfileThatIsNotAKid_IsNotAffectedByTheHouseholdLevels()
    {
        var (account, profile, _, items) = await SeedKidsCatalogueAsync();
        var context = HumanContext(account, profile);
        KidsLimitTestSupport.SetLimit(_database, "G", showUnrated: false);
        KidsLimitTestSupport.SetLimit(_database, null);

        var listed = (await CreateService(context, new StubRawProjection([], [])).LoadAuthorizedAssetsAsync(CancellationToken.None))
            .Select(row => row.AssetId).ToHashSet();

        Assert.Equal(9, listed.Count);
        Assert.Contains(items.MatureFilm.AssetId, listed);
        Assert.Contains(items.HomeVideo.AssetId, listed);
    }

    [Fact]
    public async Task KidsLevels_AMarkedExplicitItem_CountsAsAdultRated_WhateverItsRatingAndLevel()
    {
        var (account, profile, _, items) = await SeedKidsCatalogueAsync();
        var context = HumanContext(account, profile);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@work, 'explicit', 'true', CURRENT_TIMESTAMP);",
                new { work = items.FamilyFilm.WorkId });
        }

        KidsLimitTestSupport.SetLimit(_database, "R", showUnrated: true);

        var listed = (await CreateService(context, new StubRawProjection([], [])).LoadAuthorizedAssetsAsync(CancellationToken.None))
            .Select(row => row.AssetId).ToHashSet();

        Assert.DoesNotContain(items.FamilyFilm.AssetId, listed);
        Assert.Contains(items.MatureFilm.AssetId, listed);
    }
}
