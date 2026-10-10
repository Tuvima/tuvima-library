using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// The per-profile "review Shared Library contributions" (curator) flag is retired: household administrators review.
/// Upgrading drops the column once and gives everyone the new defaults; a child profile still cannot send items.
/// </summary>
public sealed class SharedLibraryCuratorRetirementTests : IDisposable
{
    private readonly List<string> _paths = [];

    [Fact]
    public void UpgradeDropsTheCuratorColumnAndAppliesTheNewDefaultsOnce()
    {
        DapperConfiguration.Configure();
        var path = NewPath();
        var adult = Guid.NewGuid();
        var child = Guid.NewGuid();
        var loose = Guid.NewGuid();
        using (var database = new DatabaseConnection(path))
        {
            database.InitializeSchema();
        }

        SqliteConnection.ClearAllPools();
        using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            raw.Open();
            Exec(raw, "ALTER TABLE profile_view_policies ADD COLUMN review_shared_library_contributions INTEGER NOT NULL DEFAULT 0;");
            SeedProfile(raw, adult, "Adult", "StandardUser");
            SeedProfile(raw, child, "Child", "RestrictedProfile");
            SeedProfile(raw, loose, "No household", "StandardUser", inHousehold: false);
            SeedPolicy(raw, adult);
            SeedPolicy(raw, child);
            SeedPolicy(raw, loose);
        }

        SqliteConnection.ClearAllPools();
        using (var upgraded = new DatabaseConnection(path))
        {
            upgraded.InitializeSchema();
            upgraded.RunStartupChecks();
        }

        Assert.Equal(0, Scalar(path, """
            SELECT COUNT(*) FROM pragma_table_info('profile_view_policies') WHERE name = 'review_shared_library_contributions';
            """));
        Assert.Equal(1, Scalar(path, "SELECT access_shared_library FROM profile_view_policies WHERE profile_id = @id;", ("@id", adult)));
        Assert.Equal(1, Scalar(path, "SELECT submit_to_shared_library FROM profile_view_policies WHERE profile_id = @id;", ("@id", adult)));
        Assert.Equal(1, Scalar(path, "SELECT access_shared_library FROM profile_view_policies WHERE profile_id = @id;", ("@id", child)));
        Assert.Equal(0, Scalar(path, "SELECT submit_to_shared_library FROM profile_view_policies WHERE profile_id = @id;", ("@id", child)));

        // A profile with no household has no Shared Library to open, so it keeps what it had.
        Assert.Equal(0, Scalar(path, "SELECT access_shared_library FROM profile_view_policies WHERE profile_id = @id;", ("@id", loose)));

        // A household administrator later turns a setting off; a second start must not undo it.
        using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            raw.Open();
            Exec(raw, "UPDATE profile_view_policies SET submit_to_shared_library = 0 WHERE profile_id = @id;", ("@id", adult));
        }

        SqliteConnection.ClearAllPools();
        using (var again = new DatabaseConnection(path))
        {
            again.InitializeSchema();
            again.RunStartupChecks();
        }

        Assert.Equal(0, Scalar(path, "SELECT submit_to_shared_library FROM profile_view_policies WHERE profile_id = @id;", ("@id", adult)));
    }

    private string NewPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima-curator-{Guid.NewGuid():N}.db");
        _paths.Add(path);
        return path;
    }

    private static readonly Guid Household = Guid.Parse("7e57a000-0000-0000-0000-000000000003");

    private static void SeedProfile(SqliteConnection raw, Guid id, string name, string role, bool inHousehold = true)
    {
        Exec(raw, "INSERT OR IGNORE INTO households(id, name, created_at) VALUES(@h, 'Test', @now);",
            ("@h", Household), ("@now", DateTimeOffset.UtcNow.ToString("O")));
        if (inHousehold)
        {
            Exec(raw, "INSERT INTO profiles(id, display_name, role, created_at, household_id) VALUES(@id, @name, @role, @now, @h);",
                ("@id", id), ("@name", name), ("@role", role), ("@now", DateTimeOffset.UtcNow.ToString("O")), ("@h", Household));
        }
        else
        {
            Exec(raw, "INSERT INTO profiles(id, display_name, role, created_at) VALUES(@id, @name, @role, @now);",
                ("@id", id), ("@name", name), ("@role", role), ("@now", DateTimeOffset.UtcNow.ToString("O")));
        }
    }

    private static void SeedPolicy(SqliteConnection raw, Guid profileId) => Exec(raw, """
        INSERT INTO profile_view_policies
            (profile_id, view_enabled, access_shared_library, submit_to_shared_library,
             review_shared_library_contributions, share_galleries, updated_at)
        VALUES (@id, 1, 0, 0, 1, 0, @now);
        """, ("@id", profileId), ("@now", DateTimeOffset.UtcNow.ToString("O")));

    private static void Exec(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value is Guid guid ? GuidSql.ToBlob(guid) : value);
        }

        command.ExecuteNonQuery();
    }

    private static long Scalar(string path, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value is Guid guid ? GuidSql.ToBlob(guid) : value);
        }

        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        foreach (var path in _paths)
        {
            TestTemp.DeleteDatabase(path);
        }
    }
}
