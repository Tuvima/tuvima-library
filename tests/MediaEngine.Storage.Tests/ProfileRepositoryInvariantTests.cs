using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage.Services;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

public sealed class ProfileRepositoryInvariantTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly ProfileRepository _repository;

    public ProfileRepositoryInvariantTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_profile_preferences_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _repository = new ProfileRepository(_database);
    }

    [Theory]
    [InlineData(ProfileRole.StandardUser)]
    [InlineData(ProfileRole.RestrictedProfile)]
    public async Task UpdateAsync_ChangesExperienceWithoutWritingPresentationRole(ProfileRole requestedRole)
    {
        var owner = Assert.IsType<Profile>(await _repository.GetByIdAsync(Profile.SeedProfileId));
        owner.DisplayName = "Updated display";
        owner.AvatarColor = "#123456";
        owner.NavigationConfig = "{\"landing\":\"listen\"}";
        owner.Role = requestedRole;

        Assert.True(await _repository.UpdateAsync(owner));

        var persisted = Assert.IsType<Profile>(await _repository.GetByIdAsync(Profile.SeedProfileId));
        Assert.Equal("Updated display", persisted.DisplayName);
        Assert.Equal("#123456", persisted.AvatarColor);
        Assert.Equal("{\"landing\":\"listen\"}", persisted.NavigationConfig);
        Assert.Equal(ProfileRole.Administrator, persisted.Role);
    }

    [Fact]
    public async Task UpdateAsync_SavesAndClearsTheBuiltInAvatarIcon()
    {
        var owner = Assert.IsType<Profile>(await _repository.GetByIdAsync(Profile.SeedProfileId));
        Assert.Null(owner.AvatarIcon);

        owner.AvatarIcon = "fox";
        Assert.True(await _repository.UpdateAsync(owner));
        Assert.Equal("fox", Assert.IsType<Profile>(await _repository.GetByIdAsync(Profile.SeedProfileId)).AvatarIcon);

        owner.AvatarIcon = null;
        Assert.True(await _repository.UpdateAsync(owner));
        Assert.Null(Assert.IsType<Profile>(await _repository.GetByIdAsync(Profile.SeedProfileId)).AvatarIcon);
    }

    public void Dispose()
    {
        _database.Dispose();
        using (var pool = new SqliteConnection($"Data Source={_databasePath}"))
        {
            SqliteConnection.ClearPool(pool);
        }

        File.Delete(_databasePath);
    }
}
