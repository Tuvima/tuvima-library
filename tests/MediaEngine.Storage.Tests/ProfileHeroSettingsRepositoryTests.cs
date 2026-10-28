using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

public sealed class ProfileHeroSettingsRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly ProfileHeroSettingsRepository _repository;

    public ProfileHeroSettingsRepositoryTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_hero_settings_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _repository = new ProfileHeroSettingsRepository(_database);
    }

    [Fact]
    public async Task APersonWhoNeverChangedAnything_GetsTheDefaults()
    {
        Assert.Equal(ProfileHeroSettings.Default, await _repository.GetAsync(SeedProfile("Alex")));
    }

    [Fact]
    public async Task SavedSettings_AreReadBackAndReplacedInTheSameRow()
    {
        var profile = SeedProfile("Alex");
        var chosen = new ProfileHeroSettings(true, 3, true, false, false, true);

        await _repository.SaveAsync(profile, chosen);
        Assert.Equal(chosen, await _repository.GetAsync(profile));

        await _repository.SaveAsync(profile, chosen with { Enabled = false });
        Assert.False((await _repository.GetAsync(profile)).Enabled);
        Assert.Equal(1, RowCount(profile));
    }

    [Fact]
    public async Task TwoProfiles_KeepTheirOwnSettings()
    {
        var alex = SeedProfile("Alex");
        var bea = SeedProfile("Bea");

        await _repository.SaveAsync(alex, ProfileHeroSettings.Default with { SlideCount = 1 });

        Assert.Equal(1, (await _repository.GetAsync(alex)).SlideCount);
        Assert.Equal(ProfileHeroSettings.Default, await _repository.GetAsync(bea));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(9, 5)]
    public async Task ASlideCountOutsideTheRange_IsKeptInside(int asked, int stored)
    {
        var profile = SeedProfile("Alex");

        await _repository.SaveAsync(profile, ProfileHeroSettings.Default with { SlideCount = asked });

        Assert.Equal(stored, (await _repository.GetAsync(profile)).SlideCount);
    }

    [Fact]
    public async Task DeletingAProfile_RemovesItsSettings()
    {
        var alex = SeedProfile("Alex");
        await _repository.SaveAsync(alex, ProfileHeroSettings.Default with { Enabled = false });

        using (var connection = _database.CreateConnection())
        {
            connection.Execute("DELETE FROM profiles WHERE id = @alex;", new { alex });
        }

        Assert.Equal(0, RowCount(alex));
    }

    [Fact]
    public async Task SavingForAProfileThatDoesNotExist_Fails()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _repository.SaveAsync(Guid.NewGuid(), ProfileHeroSettings.Default));
    }

    [Fact]
    public void TheTable_RefusesASlideCountOutsideOneToFive()
    {
        var profile = SeedProfile("Alex");
        using var connection = _database.CreateConnection();

        Assert.Throws<SqliteException>(() => connection.Execute(
            "INSERT INTO profile_hero_settings (profile_id, slide_count, updated_at) VALUES (@profile, 6, 'now');", new { profile }));
    }

    private int RowCount(Guid profileId)
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<int>(
            "SELECT COUNT(1) FROM profile_hero_settings WHERE profile_id = @profileId;", new { profileId });
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
