using Dapper;
using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

// Home's My List row (My List spec D9): the profile's saved titles from the same list and visibility rules as the My List grid.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private static string[] ShelfTitles(DisplayPageDto page, string key) =>
        page.Shelves.Single(shelf => shelf.Key == key).Items.Select(card => card.Title).ToArray();

    [Fact]
    public async Task Home_MyListRowSitsRightAfterContinueNewestSavedFirstWithoutSongs()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Watch, AccountFeatureId.Read, AccountFeatureId.Listen);
        var started = await InsertOwnedWorkWithIdAsync(library, "Halfway film", "Movie");
        var older = await InsertOwnedWorkWithIdAsync(library, "Saved older", "Movie");
        var newer = await InsertOwnedWorkWithIdAsync(library, "Saved newer", "Book");
        var song = await InsertOwnedWorkWithIdAsync(library, "Saved song", "Music");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                """
                INSERT INTO user_states(user_id, asset_id, progress_pct, last_accessed, extended_properties)
                VALUES (@profile, @asset, 40, CURRENT_TIMESTAMP, '{"position_seconds":300,"duration_seconds":900}');
                """,
                new { profile = ProfileForLookups, asset = started.AssetId });
        }

        var state = new ProfileStateRepository(_database);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Movie, older.WorkId);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Book, newer.WorkId);
        StampSaved(ProfileForLookups, older.WorkId, "2026-10-01T10:00:00.0000000+00:00");
        StampSaved(ProfileForLookups, newer.WorkId, "2026-10-02T10:00:00.0000000+00:00");
        SaveDirectly(ProfileForLookups, "Song", song.WorkId);

        var home = await CreateGridComposer(context).BuildHomeAsync(includeCatalog: false, profileId: ProfileForLookups);

        var keys = home.Shelves.Select(shelf => shelf.Key).ToArray();
        Assert.Equal("continue", keys[0]);
        Assert.Equal("my-list", keys[1]);
        Assert.Equal(["Saved newer", "Saved older"], ShelfTitles(home, "my-list"));
        var shelf = home.Shelves[1];
        Assert.Equal("My List", shelf.Title);
        Assert.Equal("/my-list", shelf.SeeAllRoute);
    }

    [Fact]
    public async Task Home_MyListRowIsFirstWhenThereIsNothingToContinue()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Watch);
        var film = await InsertOwnedWorkWithIdAsync(library, "Saved film", "Movie");
        await new ProfileStateRepository(_database).SaveItemAsync(ProfileForLookups, ProfileEntityKind.Movie, film.WorkId);

        var home = await CreateGridComposer(context).BuildHomeAsync(includeCatalog: false, profileId: ProfileForLookups);

        Assert.Equal("my-list", home.Shelves[0].Key);
        Assert.DoesNotContain(home.Shelves, shelf => shelf.Key == "continue");
    }

    [Fact]
    public async Task Home_MyListRowShowsOnlyTheNewestTwentyAndItsSeeAllShelfPageHasTheRest()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Read);
        var state = new ProfileStateRepository(_database);
        for (var index = 1; index <= 22; index++)
        {
            var book = await InsertOwnedWorkWithIdAsync(library, $"Saved book {index:00}", "Book");
            await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Book, book.WorkId);
            StampSaved(ProfileForLookups, book.WorkId, $"2026-10-{index:00}T10:00:00.0000000+00:00");
        }

        var composer = CreateGridComposer(context);
        var home = await composer.BuildHomeAsync(includeCatalog: false, profileId: ProfileForLookups, shelfLimit: 100);
        var seeAll = await composer.BuildShelfPageAsync("my-list", null, null, null, null, null, 0, 100, ProfileForLookups);

        var titles = ShelfTitles(home, "my-list");
        Assert.Equal(20, titles.Length);
        Assert.Equal("Saved book 22", titles[0]);
        Assert.Equal("Saved book 03", titles[^1]);
        Assert.Equal(22, seeAll!.Shelf.Items.Count);
        // See all is the My List page, never the removed /all/personal/my-list grid address.
        Assert.Equal("/my-list", seeAll.Shelf.SeeAllRoute);
        Assert.Equal("/my-list", home.Shelves.Single(shelf => shelf.Key == "my-list").SeeAllRoute);
    }

    [Fact]
    public async Task Home_HasNoMyListRowWhenTheListIsEmptyOrThereIsNoActiveProfile()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Watch);
        var saved = await InsertOwnedWorkWithIdAsync(library, "Saved film", "Movie");
        await InsertOwnedWorkWithIdAsync(library, "Never saved", "Movie");
        var composer = CreateGridComposer(context);

        var emptyList = await composer.BuildHomeAsync(includeCatalog: false, profileId: ProfileForLookups);
        await new ProfileStateRepository(_database).SaveItemAsync(ProfileForLookups, ProfileEntityKind.Movie, saved.WorkId);
        var withoutProfile = await composer.BuildHomeAsync(includeCatalog: false, profileId: null);

        Assert.DoesNotContain(emptyList.Shelves, shelf => shelf.Key == "my-list");
        Assert.DoesNotContain(withoutProfile.Shelves, shelf => shelf.Key == "my-list");
    }

    [Fact]
    public async Task Home_MyListRowLeavesOutASavedTitleTheProfileMayNotSeeAndAnotherProfilesSaves()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Watch);
        var family = await InsertOwnedWorkWithIdAsync(library, "Family film", "Movie");
        var adult = await InsertOwnedWorkWithIdAsync(library, "Adult film", "Movie");
        var elsewhere = await InsertOwnedWorkWithIdAsync(Guid.NewGuid(), "Other library film", "Movie");
        var others = await InsertOwnedWorkWithIdAsync(library, "Saved by someone else", "Movie");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@family, 'content_rating', 'PG', CURRENT_TIMESTAMP),
                       (@adult, 'content_rating', 'R', CURRENT_TIMESTAMP);
                """, new { family = family.WorkId, adult = adult.WorkId });
        }

        var state = new ProfileStateRepository(_database);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Movie, family.WorkId);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Movie, adult.WorkId);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Movie, elsewhere.WorkId);
        await state.SaveItemAsync(InsertOtherProfile(), ProfileEntityKind.Movie, others.WorkId);

        SetContentLimit("PG");
        var limited = await CreateGridComposer(context).BuildHomeAsync(includeCatalog: false, profileId: ProfileForLookups);
        var grid = await CreateGridComposer(context).BuildGridAsync("personal", "my-list", new DisplayGridQuery(), ProfileForLookups);

        Assert.Equal(["Family film"], ShelfTitles(limited, "my-list"));
        Assert.Equal(GridTitles(grid), ShelfTitles(limited, "my-list"));
    }
}
