using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage.Services;
using MediaEngine.TestSupport;
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
    public async Task ContentLimit_IsReadBackAndNotTouchedByTheExperienceUpdate()
    {
        var limits = new ProfileContentLimitRepository(_database);
        Assert.True((await limits.GetAsync(Profile.SeedProfileId)).IsUnrestricted);

        using (var connection = _database.CreateConnection())
        {
            Dapper.SqlMapper.Execute(connection,
                "UPDATE profiles SET content_limit='PG-13', content_limit_allow_unrated=1 WHERE id=@id",
                new { id = Profile.SeedProfileId });
        }

        var owner = Assert.IsType<Profile>(await _repository.GetByIdAsync(Profile.SeedProfileId));
        Assert.Equal("PG-13", owner.ContentLimit);
        Assert.True(owner.ContentLimitAllowUnrated);

        // The experience update never rewrites the limit, so saving a theme can't lift it.
        owner.ContentLimit = null;
        owner.DisplayName = "Renamed";
        Assert.True(await _repository.UpdateAsync(owner));

        var stored = await limits.GetAsync(Profile.SeedProfileId);
        Assert.Equal(new MediaEngine.Domain.Contracts.ProfileContentLimit("PG-13", true), stored);
        Assert.Equal(MediaEngine.Domain.Contracts.ProfileContentLimit.Strictest, await limits.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public void ContentLimit_RejectsValuesOutsideTheFiveChoices()
    {
        using var connection = _database.CreateConnection();
        Assert.Throws<SqliteException>(() => Dapper.SqlMapper.Execute(connection,
            "UPDATE profiles SET content_limit='NC-17' WHERE id=@id", new { id = Profile.SeedProfileId }));
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
        TestTemp.DeleteDatabase(_databasePath);
    }
}
