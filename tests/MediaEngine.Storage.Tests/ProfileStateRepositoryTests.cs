using Dapper;
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
        await _repository.SetReactionAsync(firstProfile, ProfileEntityKind.Movie, workId, ProfileReactionKind.Love);

        Assert.NotNull(await _repository.GetSavedItemAsync(firstProfile, ProfileEntityKind.Movie, workId));
        Assert.Equal(ProfileReactionKind.Love,
            (await _repository.GetReactionAsync(firstProfile, ProfileEntityKind.Movie, workId))?.Reaction);
        Assert.Null(await _repository.GetSavedItemAsync(secondProfile, ProfileEntityKind.Movie, workId));
        Assert.Null(await _repository.GetReactionAsync(secondProfile, ProfileEntityKind.Movie, workId));

        await _repository.RemoveSavedItemAsync(firstProfile, ProfileEntityKind.Movie, workId);
        Assert.Null(await _repository.GetSavedItemAsync(firstProfile, ProfileEntityKind.Movie, workId));
        Assert.Equal(ProfileReactionKind.Love,
            (await _repository.GetReactionAsync(firstProfile, ProfileEntityKind.Movie, workId))?.Reaction);
    }

    [Fact]
    public async Task SongCannotBeSavedButCanBeFavorited()
    {
        var profileId = SeedProfile("Listener");
        var songId = SeedWork("Music");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.SaveItemAsync(profileId, ProfileEntityKind.Song, songId));
        Assert.Contains("not saved to My List", error.Message, StringComparison.OrdinalIgnoreCase);

        var reaction = await _repository.SetReactionAsync(
            profileId, ProfileEntityKind.Song, songId, ProfileReactionKind.Like);
        Assert.Equal(ProfileReactionKind.Like, reaction.Reaction);
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
