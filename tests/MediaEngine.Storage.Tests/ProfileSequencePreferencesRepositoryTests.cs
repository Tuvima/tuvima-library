using Dapper;
using MediaEngine.Storage;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

public sealed class ProfileSequencePreferencesRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly ProfileSequencePreferencesRepository _repository;

    public ProfileSequencePreferencesRepositoryTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_profile_sequence_preferences_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _repository = new ProfileSequencePreferencesRepository(_database);
    }

    [Fact]
    public async Task SaveAsync_PersistsOnlyAnExplicitProfileAndSeriesOverride()
    {
        var profileId = SeedProfile("Owner");
        var otherProfileId = SeedProfile("Guest");

        Assert.Null(await _repository.GetAsync(profileId, "movies", "Q22092344"));

        var result = await _repository.SaveAsync(
            profileId,
            " Movies ",
            " Q22092344 ",
            showMissing: true);

        Assert.True(result.ProfileExists);
        Assert.NotNull(result.Preference);
        Assert.Equal("movies", result.Preference!.MediaType);
        Assert.Equal("q22092344", result.Preference.ContainerKey);
        Assert.True(result.Preference.ShowMissing);

        var stored = await _repository.GetAsync(profileId, "MOVIES", "q22092344");
        Assert.NotNull(stored);
        Assert.True(stored!.ShowMissing);
        Assert.Null(await _repository.GetAsync(otherProfileId, "movies", "q22092344"));
        Assert.Null(await _repository.GetAsync(profileId, "movies", "tmdb:collection:10"));
    }

    [Fact]
    public async Task DeleteAsync_RemovesOverrideSoConfigurationCanBeInheritedAgain()
    {
        var profileId = SeedProfile("Owner");
        await _repository.SaveAsync(profileId, "books", "Q123", showMissing: false);

        var deleted = await _repository.DeleteAsync(profileId, "books", "Q123");

        Assert.True(deleted);
        Assert.Null(await _repository.GetAsync(profileId, "books", "Q123"));
    }

    [Fact]
    public async Task SaveAsync_DoesNotCreateOverrideForMissingProfile()
    {
        var result = await _repository.SaveAsync(
            Guid.NewGuid(),
            "movies",
            "Q22092344",
            showMissing: true);

        Assert.False(result.ProfileExists);
        Assert.Null(result.Preference);

        using var connection = _database.CreateConnection();
        var count = connection.ExecuteScalar<long>("SELECT COUNT(1) FROM profile_sequence_preferences;");
        Assert.Equal(0, count);
    }

    private Guid SeedProfile(string displayName)
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles (id, display_name, role, created_at) VALUES (@id, @displayName, 'Administrator', @createdAt);",
            new { id, displayName, createdAt = DateTimeOffset.UtcNow.ToString("O") });
        return id;
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDatabase(_databasePath);
    }
}
