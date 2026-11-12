using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage;

namespace MediaEngine.Storage.Tests;

public sealed class ProfileStateRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly ProfileStateRepository _repository;

    public ProfileStateRepositoryTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_profile_state_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _repository = new ProfileStateRepository(_database);
    }

    [Fact]
    public async Task SavedAndReactionStates_AreIndependentAndProfileScoped()
    {
        var firstProfile = SeedProfile("First");
        var secondProfile = SeedProfile("Second");
        var workId = SeedWork("Movies");

        await _repository.SaveItemAsync(firstProfile, ProfileEntityKind.Movie, workId);
        await _repository.SetReactionAsync(firstProfile, ProfileEntityKind.Movie, workId, ProfileReactionKind.Like);

        Assert.NotNull(await _repository.GetSavedItemAsync(firstProfile, ProfileEntityKind.Movie, workId));
        Assert.Equal(ProfileReactionKind.Like,
            (await _repository.GetReactionAsync(firstProfile, ProfileEntityKind.Movie, workId))?.Reaction);
        Assert.Null(await _repository.GetSavedItemAsync(secondProfile, ProfileEntityKind.Movie, workId));
        Assert.Null(await _repository.GetReactionAsync(secondProfile, ProfileEntityKind.Movie, workId));

        await _repository.RemoveSavedItemAsync(firstProfile, ProfileEntityKind.Movie, workId);
        Assert.Null(await _repository.GetSavedItemAsync(firstProfile, ProfileEntityKind.Movie, workId));
        Assert.Equal(ProfileReactionKind.Like,
            (await _repository.GetReactionAsync(firstProfile, ProfileEntityKind.Movie, workId))?.Reaction);
    }

    [Fact]
    public async Task ASong_CanBeSavedAsAFavorite_AndLikedOrDislikedIndependently()
    {
        var profileId = SeedProfile("Listener");
        var songId = SeedWork("Music");

        await _repository.SaveItemAsync(profileId, ProfileEntityKind.Song, songId);
        Assert.Null(await _repository.GetReactionAsync(profileId, ProfileEntityKind.Song, songId));

        var liked = await _repository.SetReactionAsync(profileId, ProfileEntityKind.Song, songId, ProfileReactionKind.Like);
        Assert.Equal(ProfileReactionKind.Like, liked.Reaction);
        Assert.NotNull(await _repository.GetSavedItemAsync(profileId, ProfileEntityKind.Song, songId));

        var disliked = await _repository.SetReactionAsync(profileId, ProfileEntityKind.Song, songId, ProfileReactionKind.Dislike);
        Assert.Equal(ProfileReactionKind.Dislike, disliked.Reaction);
        Assert.NotNull(await _repository.GetSavedItemAsync(profileId, ProfileEntityKind.Song, songId));

        await _repository.RemoveReactionAsync(profileId, ProfileEntityKind.Song, songId);
        Assert.NotNull(await _repository.GetSavedItemAsync(profileId, ProfileEntityKind.Song, songId));
        await _repository.RemoveSavedItemAsync(profileId, ProfileEntityKind.Song, songId);
        Assert.Null(await _repository.GetSavedItemAsync(profileId, ProfileEntityKind.Song, songId));
    }

    [Fact]
    public async Task SavedCollection_IsOneContainerReference()
    {
        var profileId = SeedProfile("Collector");
        var collectionId = SeedCollection(profileId);

        await _repository.SaveItemAsync(profileId, ProfileEntityKind.Collection, collectionId);

        var saved = await _repository.GetSavedItemsAsync(profileId);
        Assert.Collection(saved, item =>
        {
            Assert.Equal(ProfileEntityKind.Collection, item.EntityKind);
            Assert.Equal(collectionId, item.EntityId);
        });
    }

    [Fact]
    public void FreshSchema_HasFirstClassProfileStateAndNoRequiredCollectionForeignKey()
    {
        using var connection = _database.CreateConnection();
        var tables = connection.Query<string>(
            "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('profile_saved_items','profile_reactions');")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("profile_saved_items", tables);
        Assert.Contains("profile_reactions", tables);

        var savedColumns = connection.Query<string>("SELECT name FROM pragma_table_info('profile_saved_items');").ToList();
        Assert.DoesNotContain("collection_id", savedColumns, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemoveSavedItems_ReturnsTheOriginalsSkipsUnsavedItemsAndLeavesAnotherProfilesListAlone()
    {
        var profileId = SeedProfile("First");
        var otherProfile = SeedProfile("Second");
        var first = SeedWork("Movies");
        var second = SeedWork("Movies");
        var notSaved = SeedWork("Movies");
        await _repository.SaveItemAsync(profileId, ProfileEntityKind.Movie, first, position: 4);
        await _repository.SaveItemAsync(profileId, ProfileEntityKind.Movie, second);
        await _repository.SaveItemAsync(otherProfile, ProfileEntityKind.Movie, first);
        var firstSavedAt = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
        await _repository.RestoreSavedItemsAsync(profileId, [new ProfileSavedRestore(ProfileEntityKind.Movie, first, firstSavedAt, 4)]);

        var removed = await _repository.RemoveSavedItemsAsync(profileId,
        [
            new ProfileSavedKey(ProfileEntityKind.Movie, first),
            new ProfileSavedKey(ProfileEntityKind.Movie, notSaved),
            new ProfileSavedKey(ProfileEntityKind.Movie, Guid.NewGuid()),
            new ProfileSavedKey(ProfileEntityKind.Movie, first),
        ]);

        var item = Assert.Single(removed);
        Assert.Equal(first, item.EntityId);
        Assert.Equal(firstSavedAt, item.SavedAt);
        Assert.Equal(4, item.Position);
        Assert.Null(await _repository.GetSavedItemAsync(profileId, ProfileEntityKind.Movie, first));
        Assert.NotNull(await _repository.GetSavedItemAsync(profileId, ProfileEntityKind.Movie, second));
        Assert.NotNull(await _repository.GetSavedItemAsync(otherProfile, ProfileEntityKind.Movie, first));
    }

    [Fact]
    public async Task RestoreSavedItems_KeepsTheGivenSavedDatesAndPositionsAndSkipsTitlesThatNoLongerExist()
    {
        var profileId = SeedProfile("First");
        var otherProfile = SeedProfile("Second");
        var kept = SeedWork("Movies");
        var older = DateTimeOffset.Parse("2026-09-01T08:00:00Z");

        var restored = await _repository.RestoreSavedItemsAsync(profileId,
        [
            new ProfileSavedRestore(ProfileEntityKind.Movie, kept, older, 7),
            new ProfileSavedRestore(ProfileEntityKind.Movie, Guid.NewGuid(), older, null),
        ]);

        var item = Assert.Single(restored);
        Assert.Equal(kept, item.EntityId);
        var stored = await _repository.GetSavedItemAsync(profileId, ProfileEntityKind.Movie, kept);
        Assert.Equal(older, stored!.SavedAt);
        Assert.Equal(7, stored.Position);
        Assert.Empty(await _repository.GetSavedItemsAsync(otherProfile));
    }

    [Fact]
    public async Task RestoreSavedItems_PutsAnItemSavedAgainInTheMeantimeBackAtItsOriginalDate()
    {
        var profileId = SeedProfile("First");
        var workId = SeedWork("Movies");
        await _repository.SaveItemAsync(profileId, ProfileEntityKind.Movie, workId);
        var older = DateTimeOffset.Parse("2026-09-01T08:00:00Z");

        await _repository.RestoreSavedItemsAsync(profileId, [new ProfileSavedRestore(ProfileEntityKind.Movie, workId, older, null)]);

        var stored = Assert.Single(await _repository.GetSavedItemsAsync(profileId));
        Assert.Equal(older, stored.SavedAt);
    }

    private Guid SeedProfile(string name)
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles(id, display_name, role, created_at) VALUES (@id, @name, 'StandardUser', @createdAt);",
            new { id, name, createdAt = DateTimeOffset.UtcNow.ToString("O") });
        return id;
    }

    private Guid SeedWork(string mediaType)
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("INSERT INTO works(id, media_type) VALUES (@id, @mediaType);", new { id, mediaType });
        return id;
    }

    private Guid SeedCollection(Guid profileId)
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO collections(id, display_name, collection_type, scope, profile_id, resolution)
            VALUES (@id, 'Personal picks', 'Custom', 'user', @profileId, 'materialized');
            """,
            new { id, profileId });
        return id;
    }

    public void Dispose()
    {
        _database.Dispose();
        try { File.Delete(_databasePath); }
        catch (IOException) { }
    }
}
