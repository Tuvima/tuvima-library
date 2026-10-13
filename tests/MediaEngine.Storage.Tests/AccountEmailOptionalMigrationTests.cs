using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// An account made on this computer has no email until it is secured, so <c>accounts.email</c> and
/// <c>normalized_email</c> allow NULL. A data store from before that rule has them required; the startup migration
/// rebuilds the table once without losing a row, a link or the unique index.
/// </summary>
public sealed class AccountEmailOptionalMigrationTests : IDisposable
{
    private static readonly Guid AdminAccount = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid PersonAccount = Guid.Parse("a0000000-0000-0000-0000-000000000002");
    private static readonly Guid AdminProfile = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly string Now = DateTimeOffset.UtcNow.ToString("O");

    private readonly List<string> _paths = [];

    [Fact]
    public void ANewDataStoreAllowsAccountsWithoutAnEmail()
    {
        var path = NewPath();
        using (var database = new DatabaseConnection(path))
        {
            database.InitializeSchema();
            database.RunStartupChecks();
        }

        Assert.Equal(0, NotNull(path, "email"));
        Assert.Equal(0, NotNull(path, "normalized_email"));
    }

    [Fact]
    public void ARequiredEmailBecomesOptional_AndEveryRowAndLinkSurvives()
    {
        var path = CreateRequiredEmailDatabase();

        using (var database = Open(path))
        {
            Assert.Empty(database.StartupNotes);
        }

        Assert.Equal(0, NotNull(path, "email"));
        Assert.Equal(0, NotNull(path, "normalized_email"));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM accounts WHERE id = @a AND email = 'owner@example.com' AND normalized_email = 'OWNER@EXAMPLE.COM' AND is_administrator = 1 AND this_computer_only = 0 AND household_admin = 1;", ("@a", AdminAccount)));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM accounts WHERE id = @a AND grants_inherit_from_account_id = @p AND must_change_password = 1;", ("@a", PersonAccount), ("@p", AdminAccount)));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM account_profile_grants WHERE account_id = @a;", ("@a", AdminAccount)));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM account_credentials WHERE account_id = @a;", ("@a", AdminAccount)));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM auth_sessions WHERE account_id = @a;", ("@a", AdminAccount)));
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'accounts_rebuild';"));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ux_accounts_normalized_email';"));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_accounts_household';"));
    }

    [Fact]
    public void RunningTheMigrationTwiceChangesNothing()
    {
        var path = CreateRequiredEmailDatabase();
        using (Open(path))
        {
        }

        var versionBefore = Scalar(path, "SELECT authorization_version FROM accounts WHERE id = @a;", ("@a", AdminAccount));
        using (var database = Open(path))
        {
            Assert.Empty(database.StartupNotes);
        }

        Assert.Equal(2, Scalar(path, "SELECT COUNT(*) FROM accounts;"));
        Assert.Equal(versionBefore, Scalar(path, "SELECT authorization_version FROM accounts WHERE id = @a;", ("@a", AdminAccount)));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM account_profile_grants WHERE account_id = @a;", ("@a", AdminAccount)));
        Assert.Equal(0, NotNull(path, "email"));
    }

    [Fact]
    public void ManyAccountsMayHaveNoEmail_ButTwoMayNotShareOne()
    {
        var path = CreateRequiredEmailDatabase();
        using (Open(path))
        {
        }

        using var raw = new SqliteConnection($"Data Source={path};Pooling=False");
        raw.Open();
        Exec(raw, "INSERT INTO accounts(id, email, normalized_email, created_at, updated_at) VALUES(@a, NULL, NULL, @now, @now);", ("@a", Guid.NewGuid()), ("@now", Now));
        Exec(raw, "INSERT INTO accounts(id, email, normalized_email, created_at, updated_at) VALUES(@a, NULL, NULL, @now, @now);", ("@a", Guid.NewGuid()), ("@now", Now));
        Assert.Throws<SqliteException>(() => Exec(raw,
            "INSERT INTO accounts(id, email, normalized_email, created_at, updated_at) VALUES(@a, 'x@example.com', 'OWNER@EXAMPLE.COM', @now, @now);",
            ("@a", Guid.NewGuid()), ("@now", Now)));
    }

    private string NewPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima-account-email-{Guid.NewGuid():N}.db");
        _paths.Add(path);
        return path;
    }

    private static DatabaseConnection Open(string path)
    {
        var database = new DatabaseConnection(path);
        try
        {
            database.InitializeSchema();
            database.RunStartupChecks();
            return database;
        }
        catch
        {
            database.Dispose();
            SqliteConnection.ClearAllPools();
            throw;
        }
    }

    /// <summary>Builds a current data store, then puts <c>accounts</c> back into the shape that required an email, and seeds it.</summary>
    private string CreateRequiredEmailDatabase()
    {
        var path = NewPath();
        using (var database = new DatabaseConnection(path))
        {
            database.InitializeSchema();
        }

        SqliteConnection.ClearAllPools();
        using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            raw.Open();
            Exec(raw, "PRAGMA foreign_keys = OFF;");
            Exec(raw, """
                CREATE TABLE accounts_required (
                    id               BLOB NOT NULL PRIMARY KEY,
                    email            TEXT NOT NULL,
                    normalized_email TEXT NOT NULL,
                    is_enabled       INTEGER NOT NULL DEFAULT 1 CHECK (is_enabled IN (0, 1)),
                    is_administrator INTEGER NOT NULL DEFAULT 0 CHECK (is_administrator IN (0, 1)),
                    authorization_version INTEGER NOT NULL DEFAULT 1 CHECK (authorization_version > 0),
                    created_at       TEXT NOT NULL,
                    updated_at       TEXT NOT NULL,
                    household_id     BLOB REFERENCES households(id),
                    household_admin  INTEGER NOT NULL DEFAULT 0 CHECK (household_admin IN (0, 1)),
                    grants_inherit_from_account_id BLOB REFERENCES accounts(id) ON DELETE SET NULL,
                    this_computer_only INTEGER NOT NULL DEFAULT 0 CHECK (this_computer_only IN (0, 1)),
                    must_change_password INTEGER NOT NULL DEFAULT 0 CHECK (must_change_password IN (0, 1)),
                    temporary_password_expires_at TEXT
                );
                DROP TABLE accounts;
                ALTER TABLE accounts_required RENAME TO accounts;
                CREATE UNIQUE INDEX ux_accounts_normalized_email ON accounts(normalized_email);
                """);
            Exec(raw, "PRAGMA foreign_keys = ON;");
            Exec(raw, "INSERT INTO profiles(id, display_name, role, created_at) VALUES(@p, 'Owner', 'Administrator', @now);",
                ("@p", AdminProfile), ("@now", Now));
            Exec(raw, """
                INSERT INTO accounts(id, email, normalized_email, is_enabled, is_administrator, authorization_version, created_at, updated_at, household_admin)
                VALUES(@a, 'owner@example.com', 'OWNER@EXAMPLE.COM', 1, 1, 1, @now, @now, 1);
                """, ("@a", AdminAccount), ("@now", Now));
            Exec(raw, """
                INSERT INTO accounts(id, email, normalized_email, is_enabled, authorization_version, created_at, updated_at, grants_inherit_from_account_id, must_change_password)
                VALUES(@a, 'person@example.com', 'PERSON@EXAMPLE.COM', 1, 1, @now, @now, @parent, 1);
                """, ("@a", PersonAccount), ("@parent", AdminAccount), ("@now", Now));
            Exec(raw, "INSERT INTO account_profile_grants(account_id, profile_id, is_default, is_enabled, admin_enabled, authorization_version, granted_at) VALUES(@a, @p, 1, 1, 1, 1, @now);",
                ("@a", AdminAccount), ("@p", AdminProfile), ("@now", Now));
            Exec(raw, """
                INSERT INTO account_credentials(id, account_id, credential_kind, secret_hash, hash_scheme, hash_version, security_stamp, created_at, updated_at)
                VALUES(@id, @a, 'Password', 'hash', 'scheme', 1, 'stamp', @now, @now);
                """, ("@id", Guid.NewGuid()), ("@a", AdminAccount), ("@now", Now));
            Exec(raw, """
                INSERT INTO auth_sessions(id, account_id, active_profile_id, token_hash, device_id, device_name, client, authentication_method, issued_ingress, security_stamp, created_at, last_seen_at, expires_at)
                VALUES(@id, @a, @p, 'token', 'device', 'Device', 'Dashboard', 'Password', 'home_network', 'stamp', @now, @now, @later);
                """, ("@id", Guid.NewGuid()), ("@a", AdminAccount), ("@p", AdminProfile), ("@now", Now),
                ("@later", DateTimeOffset.UtcNow.AddDays(1).ToString("O")));
        }

        SqliteConnection.ClearAllPools();
        return path;
    }

    private static long NotNull(string path, string column) =>
        Scalar(path, $"SELECT \"notnull\" FROM pragma_table_info('accounts') WHERE name = '{column}';");

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

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in _paths)
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var file = path + suffix;
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
        }
    }
}
