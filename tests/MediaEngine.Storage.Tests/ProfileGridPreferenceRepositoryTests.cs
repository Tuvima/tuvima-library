using Dapper;
using MediaEngine.Storage;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

public sealed class ProfileGridPreferenceRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly ProfileGridPreferenceRepository _repository;

    public ProfileGridPreferenceRepositoryTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_grid_prefs_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _repository = new ProfileGridPreferenceRepository(_database);
    }

    [Fact]
    public async Task ASavedSort_IsReadBackForThatGridOnly()
    {
        var profile = SeedProfile("Alex");

        await _repository.SaveSortAsync(profile, "watch", "title");

        var sorts = await _repository.GetSortsAsync(profile);
        Assert.Equal("title", Assert.Single(sorts).Value);
        Assert.Equal("title", sorts["watch"]);
        Assert.False(sorts.ContainsKey("read"));
    }

    [Fact]
    public async Task APersonWithNothingSaved_GetsAnEmptyMap()
    {
        var profile = SeedProfile("Alex");

        Assert.Empty(await _repository.GetSortsAsync(profile));
    }

    [Fact]
    public async Task SavingAgain_ReplacesTheSortInTheSameRow()
    {
        var profile = SeedProfile("Alex");

        await _repository.SaveSortAsync(profile, "listen", "added");
        await _repository.SaveSortAsync(profile, "listen", "played");

        Assert.Equal("played", (await _repository.GetSortsAsync(profile))["listen"]);
        Assert.Equal(1, RowCount(profile));
    }

    [Fact]
    public async Task TwoProfiles_KeepTheirOwnSorts()
    {
        var alex = SeedProfile("Alex");
        var bea = SeedProfile("Bea");

        await _repository.SaveSortAsync(alex, "read", "year");
        await _repository.SaveSortAsync(bea, "read", "title");

        Assert.Equal("year", (await _repository.GetSortsAsync(alex))["read"]);
        Assert.Equal("title", (await _repository.GetSortsAsync(bea))["read"]);
    }

    [Fact]
    public async Task DeletingAProfile_RemovesItsSortsAndNobodyElses()
    {
        var alex = SeedProfile("Alex");
        var bea = SeedProfile("Bea");
        await _repository.SaveSortAsync(alex, "liked", "added");
        await _repository.SaveSortAsync(bea, "liked", "added");

        using (var connection = _database.CreateConnection())
        {
            connection.Execute("DELETE FROM profiles WHERE id = @alex;", new { alex });
        }

        Assert.Empty(await _repository.GetSortsAsync(alex));
        Assert.Single(await _repository.GetSortsAsync(bea));
    }

    [Fact]
    public async Task SavingForAProfileThatDoesNotExist_Fails()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _repository.SaveSortAsync(Guid.NewGuid(), "watch", "title"));
    }

    [Theory]
    [InlineData("videos", "title")]
    [InlineData("watch", "chaos")]
    public async Task TheTable_RefusesAnUnknownGridOrSort(string gridKey, string sort)
    {
        var profile = SeedProfile("Alex");

        await Assert.ThrowsAsync<SqliteException>(() => _repository.SaveSortAsync(profile, gridKey, sort));
        Assert.Equal(0, RowCount(profile));
    }

    private int RowCount(Guid profileId)
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<int>(
            "SELECT COUNT(1) FROM profile_grid_preferences WHERE profile_id = @profileId;", new { profileId });
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

    public void Dispose()
    {
        _database.Dispose();
        try { File.Delete(_databasePath); }
        catch (IOException) { }
    }
}
