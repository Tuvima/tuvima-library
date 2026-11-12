using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

// Favorites (the profile's hearted songs) are kept in an order the person sets by hand: the move action is the signed-in profile's own,
// counts the places the profile can see, and refuses a song that is not one of its Favorites (Listen music spec D14).
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private async Task<(DefaultHttpContext Context, ProfileStateRepository State, List<Guid> Songs)> SeedFavoriteSongsAsync(
        int count,
        params Guid[] hiddenLibraries)
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, hiddenLibraries.ToHashSet());
        var state = new ProfileStateRepository(_database);
        var songs = new List<Guid>();
        for (var index = 0; index < count; index++)
        {
            songs.Add((await InsertOwnedWorkWithIdAsync(MoviesLibrary, $"Favorite song {index}", "Music")).WorkId);
        }

        return (HumanContext(account, ProfileForLookups), state, songs);
    }

    private Task<(int Status, string Body)> MoveFavoriteAsync(DefaultHttpContext context, Guid songId, int toIndex)
        => CallAsync(
            app => app.MapProfileStateEndpoints(),
            services => ProfileStateServices(services, context),
            "MoveFavoriteSong", context, "POST", new { entityId = songId }, jsonBody: $"{{\"toIndex\":{toIndex}}}");

    private static async Task<List<Guid>> FavoritesOf(ProfileStateRepository state, Guid profile)
        => (await state.GetSavedItemsAsync(profile, ProfileEntityKind.Song)).Select(item => item.EntityId).ToList();

    [Fact]
    public async Task MovingAFavoriteSong_ChangesTheSavedOrderOfTheSignedInProfileOnly_AndTheReadReturnsIt()
    {
        var world = await SeedFavoriteSongsAsync(3);
        var (a, b, c) = (world.Songs[0], world.Songs[1], world.Songs[2]);
        var otherProfile = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO profiles(id, display_name, role, created_at) VALUES (@otherProfile, 'Other', 'StandardUser', CURRENT_TIMESTAMP);",
                new { otherProfile });
        }

        foreach (var song in new[] { c, b, a })
        {
            await world.State.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Song, song);
            await world.State.SaveItemAsync(otherProfile, ProfileEntityKind.Song, song);
        }

        var moved = await MoveFavoriteAsync(world.Context, c, 0);

        Assert.Equal(StatusCodes.Status204NoContent, moved.Status);
        Assert.Equal([c, a, b], await FavoritesOf(world.State, ProfileForLookups));
        Assert.Equal([a, b, c], await FavoritesOf(world.State, otherProfile));
        var read = await CallAsync(
            app => app.MapProfileStateEndpoints(), services => ProfileStateServices(services, world.Context),
            "GetSavedProfileItems", world.Context, "GET");
        Assert.True(read.Body.IndexOf(c.ToString("D"), StringComparison.OrdinalIgnoreCase) < read.Body.IndexOf(a.ToString("D"), StringComparison.OrdinalIgnoreCase));
        Assert.True(read.Body.IndexOf(a.ToString("D"), StringComparison.OrdinalIgnoreCase) < read.Body.IndexOf(b.ToString("D"), StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(40)]
    public async Task MovingAFavoriteSongToAPlaceOutsideTheList_IsRefused_AndChangesNothing(int toIndex)
    {
        var world = await SeedFavoriteSongsAsync(2);
        await world.State.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Song, world.Songs[1]);
        await world.State.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Song, world.Songs[0]);

        var moved = await MoveFavoriteAsync(world.Context, world.Songs[0], toIndex);

        Assert.Equal(StatusCodes.Status400BadRequest, moved.Status);
        Assert.Equal([world.Songs[0], world.Songs[1]], await FavoritesOf(world.State, ProfileForLookups));
    }

    [Fact]
    public async Task MovingASongThatIsNotOneOfTheProfilesFavorites_AnswersNotFound_EvenWhenItExistsInTheLibrary()
    {
        var world = await SeedFavoriteSongsAsync(2);
        await world.State.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Song, world.Songs[0]);

        var notHearted = await MoveFavoriteAsync(world.Context, world.Songs[1], 0);
        var unknown = await MoveFavoriteAsync(world.Context, Guid.NewGuid(), 0);

        Assert.Equal(StatusCodes.Status404NotFound, notHearted.Status);
        Assert.Equal(StatusCodes.Status404NotFound, unknown.Status);
        Assert.Equal([world.Songs[0]], await FavoritesOf(world.State, ProfileForLookups));
    }

    [Fact]
    public async Task APlaceInFavorites_CountsOnlyTheSongsTheProfileCanSee_SoAHiddenFavoriteNeverShiftsTheMove()
    {
        var hiddenLibrary = Guid.NewGuid();
        var world = await SeedFavoriteSongsAsync(3, hiddenLibrary);
        var (first, second, third) = (world.Songs[0], world.Songs[1], world.Songs[2]);
        var hidden = (await InsertOwnedWorkWithIdAsync(hiddenLibrary, "Hidden favorite", "Music")).WorkId;
        foreach (var song in new[] { third, hidden, second, first })
        {
            await world.State.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Song, song);
        }

        // The profile sees first, second, third (the hidden song sits between second and third and is never counted).
        var moved = await MoveFavoriteAsync(world.Context, first, 2);

        Assert.Equal(StatusCodes.Status204NoContent, moved.Status);
        Assert.Equal([second, hidden, third, first], await FavoritesOf(world.State, ProfileForLookups));
    }
}
