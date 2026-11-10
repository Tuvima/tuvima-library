using Dapper;
using MediaEngine.Api.Services.Details.Internals;
using MediaEngine.Api.Services.Display;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage;
using MediaEngine.TestSupport;

namespace MediaEngine.Api.Tests;

/// <summary>A profile's Favorites are the songs it saved (the heart); Like and Dislike are reactions and never make a favorite.</summary>
public sealed class DisplayFavoriteProjectionReaderTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_favorites_reader_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly ProfileStateRepository _state;
    private readonly DisplayFavoriteProjectionReader _reader;
    private readonly Guid _profile = Guid.NewGuid();
    private readonly Guid _otherProfile = Guid.NewGuid();

    public DisplayFavoriteProjectionReaderTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _state = new ProfileStateRepository(_database);
        _reader = new DisplayFavoriteProjectionReader(_database);
        SeedProfile(_profile);
        SeedProfile(_otherProfile);
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDatabase(_databasePath);
    }

    [Fact]
    public async Task ASavedSong_IsAFavorite_AndASongOnlyLikedOrDislikedIsNot()
    {
        var saved = SeedWork("Music");
        var liked = SeedWork("Music");
        var disliked = SeedWork("Music");
        await _state.SaveItemAsync(_profile, ProfileEntityKind.Song, saved);
        await _state.SetReactionAsync(_profile, ProfileEntityKind.Song, liked, ProfileReactionKind.Like);
        await _state.SetReactionAsync(_profile, ProfileEntityKind.Song, disliked, ProfileReactionKind.Dislike);

        var favorites = await _reader.LoadAsync(_profile, CancellationToken.None);

        Assert.Equal([saved], favorites);
    }

    [Fact]
    public async Task ASongBothSavedAndLiked_IsAFavorite_AndSavingNeverClearsTheLike()
    {
        var song = SeedWork("Music");
        await _state.SetReactionAsync(_profile, ProfileEntityKind.Song, song, ProfileReactionKind.Like);
        await _state.SaveItemAsync(_profile, ProfileEntityKind.Song, song);

        Assert.Equal([song], await _reader.LoadAsync(_profile, CancellationToken.None));
        Assert.Equal(ProfileReactionKind.Like, (await _state.GetReactionAsync(_profile, ProfileEntityKind.Song, song))?.Reaction);

        await _state.RemoveSavedItemAsync(_profile, ProfileEntityKind.Song, song);

        Assert.Empty(await _reader.LoadAsync(_profile, CancellationToken.None));
        Assert.Equal(ProfileReactionKind.Like, (await _state.GetReactionAsync(_profile, ProfileEntityKind.Song, song))?.Reaction);
    }

    [Fact]
    public async Task FavoritesAreSongsOnly_AndEachProfileHasItsOwn()
    {
        var song = SeedWork("Music");
        var movie = SeedWork("Movies");
        await _state.SaveItemAsync(_profile, ProfileEntityKind.Song, song);
        await _state.SaveItemAsync(_profile, ProfileEntityKind.Movie, movie);

        Assert.Equal([song], await _reader.LoadAsync(_profile, CancellationToken.None));
        Assert.Empty(await _reader.LoadAsync(_otherProfile, CancellationToken.None));
        Assert.Empty(await _reader.LoadAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task TheDetailsPageHeartAndThePlayersHeart_ReadTheSameSavedItems()
    {
        var movie = SeedWork("Movies");
        var song = SeedWork("Music");
        await _state.SaveItemAsync(_profile, ProfileEntityKind.Movie, movie);
        await _state.SaveItemAsync(_profile, ProfileEntityKind.Song, song);

        var details = await new DetailProjectionReader(_database, null!).LoadFavoriteWorkIdsAsync(_profile, CancellationToken.None);

        Assert.True(details.SetEquals([movie, song]));
        Assert.Empty(await new DetailProjectionReader(_database, null!).LoadFavoriteWorkIdsAsync(_otherProfile, CancellationToken.None));
    }

    private void SeedProfile(Guid id)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles(id, display_name, role, created_at) VALUES (@id, 'Listener', 'StandardUser', @createdAt);",
            new { id, createdAt = DateTimeOffset.UtcNow.ToString("O") });
    }

    private Guid SeedWork(string mediaType)
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("INSERT INTO works(id, media_type) VALUES (@id, @mediaType);", new { id, mediaType });
        return id;
    }
}
