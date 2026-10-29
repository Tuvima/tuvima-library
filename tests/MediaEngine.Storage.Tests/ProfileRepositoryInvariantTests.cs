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
    public async Task ContentLimit_IsTheHouseholdsKidSettingsForAKidsProfile_ReadLive_AndNoneForAnAdult()
    {
        var limits = new ProfileContentLimitRepository(_database);
        Assert.True((await limits.GetAsync(Profile.SeedProfileId)).IsUnrestricted);

        var household = Guid.NewGuid();
        var kid = Guid.NewGuid();
        var adult = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            Dapper.SqlMapper.Execute(connection,
                """
                INSERT INTO households(id,name,created_at,is_default,kid_watch_level,kid_read_level,kid_listen_level,kid_show_unrated)
                VALUES(@household,'Home','2026-10-29T00:00:00.0000000+00:00',0,'PG-13','G','R',0);
                INSERT INTO profiles(id,display_name,avatar_color,role,created_at,household_id)
                VALUES(@kid,'Kid','#7C4DFF','RestrictedProfile','2026-10-29T00:00:00.0000000+00:00',@household),
                      (@adult,'Adult','#7C4DFF','StandardUser','2026-10-29T00:00:00.0000000+00:00',@household);
                """,
                new { household, kid, adult });
        }

        Assert.Equal(
            new MediaEngine.Domain.Contracts.ProfileContentLimit(
                true, new MediaEngine.Domain.Contracts.HouseholdKidSettings("PG-13", "G", "R", false)),
            await limits.GetAsync(kid));
        Assert.True((await limits.GetAsync(adult)).IsUnrestricted);

        // The Kid's profile holds no copy: changing the household changes what it reads on the very next call.
        using (var connection = _database.CreateConnection())
        {
            Dapper.SqlMapper.Execute(connection,
                "UPDATE households SET kid_watch_level='G', kid_show_unrated=1 WHERE id=@household", new { household });
        }

        var live = await limits.GetAsync(kid);
        Assert.Equal("G", live.Settings.WatchLevel);
        Assert.True(live.Settings.ShowUnrated);
        Assert.Equal(MediaEngine.Domain.Contracts.ProfileContentLimit.Strictest, await limits.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task HouseholdKidSettings_StartOnPgWithUnratedShown_AreReplacedAsAWhole_AndRejectLevelsOffTheLadder()
    {
        var household = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            Dapper.SqlMapper.Execute(connection,
                "INSERT INTO households(id,name,created_at,is_default) VALUES(@household,'Home','2026-10-29T00:00:00.0000000+00:00',0);",
                new { household });
        }

        var settings = new HouseholdKidSettingsRepository(_database);
        Assert.Equal(MediaEngine.Domain.Contracts.HouseholdKidSettings.Default, await settings.GetAsync(household));
        Assert.Null(await settings.GetAsync(Guid.NewGuid()));

        Assert.True(await settings.ReplaceAsync(household, new MediaEngine.Domain.Contracts.HouseholdKidSettings("G", "R", "PG-13", false)));
        Assert.Equal(new MediaEngine.Domain.Contracts.HouseholdKidSettings("G", "R", "PG-13", false), await settings.GetAsync(household));
        Assert.False(await settings.ReplaceAsync(Guid.NewGuid(), MediaEngine.Domain.Contracts.HouseholdKidSettings.Default));

        using var check = _database.CreateConnection();
        Assert.Throws<SqliteException>(() => Dapper.SqlMapper.Execute(check,
            "UPDATE households SET kid_watch_level='NC-17' WHERE id=@household", new { household }));
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
