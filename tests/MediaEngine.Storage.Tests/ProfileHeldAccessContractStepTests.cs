using Dapper;
using MediaEngine.Domain.Aggregates;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// Users, profiles and households (contract wave): the sign-in-held access model is gone from the data structure. A user (accounts row)
/// is only a sign-in attached to exactly one profile.
/// </summary>
public sealed class ProfileHeldAccessContractStepTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_contract_step_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly string _now = new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero).ToString("O");

    public ProfileHeldAccessContractStepTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
    }

    [Theory]
    [InlineData("account_profile_grants")]
    [InlineData("account_feature_grants")]
    [InlineData("account_library_grants")]
    [InlineData("grant_admin_protections")]
    [InlineData("grant_admin_unlocks")]
    public void TheSignInHeldAccessTables_AreGone(string table)
    {
        using var connection = _database.CreateConnection();

        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @table;", new { table }));
    }

    [Fact]
    public void TheSignInHeldRoleAndHouseholdColumns_AreGone()
    {
        using var connection = _database.CreateConnection();

        var accountColumns = Columns(connection, "accounts");
        Assert.DoesNotContain("is_administrator", accountColumns);
        Assert.DoesNotContain("household_admin", accountColumns);
        Assert.DoesNotContain("household_id", accountColumns);
        Assert.DoesNotContain("grants_inherit_from_account_id", accountColumns);
        Assert.DoesNotContain("primary_account_id", Columns(connection, "households"));
        Assert.Contains("is_default", Columns(connection, "households"));
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'ix_accounts_household';"));
    }

    [Fact]
    public void AUserCannotExistWithoutItsProfile_AndAProfileHasAtMostOneUser()
    {
        using var connection = _database.CreateConnection();

        Assert.Equal(1, connection.ExecuteScalar<int>("SELECT \"notnull\" FROM pragma_table_info('accounts') WHERE name = 'profile_id';"));
        Assert.Throws<SqliteException>(() => connection.Execute(
            "INSERT INTO accounts(id,email,normalized_email,created_at,updated_at) VALUES(@id,'a@example.com','A@EXAMPLE.COM',@now,@now);",
            new { id = Guid.NewGuid(), now = _now }));

        InsertUser(connection, Profile.SeedProfileId, "owner@example.com");
        Assert.Throws<SqliteException>(() => InsertUser(connection, Profile.SeedProfileId, "second@example.com"));
    }

    [Fact]
    public void DeletingAProfile_DeletesItsUser()
    {
        using var connection = _database.CreateConnection();
        var profileId = Guid.NewGuid();
        connection.Execute(
            "INSERT INTO profiles(id,display_name,avatar_color,role,created_at) VALUES(@profileId,'Sam','#7C4DFF','StandardUser',@now);",
            new { profileId, now = _now });
        InsertUser(connection, profileId, "sam@example.com");

        connection.Execute("DELETE FROM profiles WHERE id = @profileId;", new { profileId });

        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM accounts WHERE profile_id = @profileId;", new { profileId }));
    }

    private void InsertUser(SqliteConnection connection, Guid profileId, string email) => connection.Execute(
        "INSERT INTO accounts(id,email,normalized_email,created_at,updated_at,profile_id) VALUES(@id,@email,@normalized,@now,@now,@profileId);",
        new { id = Guid.NewGuid(), email, normalized = email.ToUpperInvariant(), now = _now, profileId });

    private static List<string> Columns(SqliteConnection connection, string table) =>
        connection.Query<string>($"SELECT name FROM pragma_table_info('{table}');").ToList();

    public void Dispose()
    {
        _database.Dispose();
        MediaEngine.TestSupport.TestTemp.DeleteDatabase(_databasePath);
    }
}
