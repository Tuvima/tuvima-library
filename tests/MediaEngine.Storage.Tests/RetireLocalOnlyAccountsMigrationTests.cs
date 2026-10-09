using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// Every account signs in with an email. A data store from before that rule has email-less "local-only" accounts;
/// the startup migration moves their profiles into the server administrator's household, removes those accounts,
/// and rebuilds <c>accounts</c> with a required email.
/// </summary>
public sealed class RetireLocalOnlyAccountsMigrationTests : IDisposable
{
    private static readonly Guid AdminAccount = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid LocalAccount = Guid.Parse("b0000000-0000-0000-0000-000000000001");
    private static readonly Guid AdminProfile = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid KidsProfile = Guid.Parse("c0000000-0000-0000-0000-000000000002");
    private static readonly Guid GrandmaProfile = Guid.Parse("c0000000-0000-0000-0000-000000000003");

    private readonly List<string> _paths = [];

    [Fact]
    public void ProfilesMoveToTheAdministratorAndHistoryStaysAttached()
    {
        var path = CreateLegacyDatabase(seed: raw =>
        {
            SeedAdministrator(raw);
            SeedLocalOnlyAccount(raw, KidsProfile, "Kids", GrandmaProfile, "Grandma");
        });

        using (var database = Open(path))
        {
            Assert.Equal(2, database.StartupNotes.Count);
            Assert.Contains(database.StartupNotes, note => note.Contains("'Kids'", StringComparison.Ordinal));
            Assert.Contains(database.StartupNotes, note => note.Contains("'Grandma'", StringComparison.Ordinal));
        }

        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM accounts;"));
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM pragma_table_info('accounts') WHERE name = 'is_local_only';"));
        Assert.Equal(1, Scalar(path, "SELECT \"notnull\" FROM pragma_table_info('accounts') WHERE name = 'email';"));
        Assert.Equal(1, Scalar(path, "SELECT \"notnull\" FROM pragma_table_info('accounts') WHERE name = 'normalized_email';"));

        // The administrator's household now holds the original profile plus the two moved ones.
        Assert.Equal(3, Scalar(path, "SELECT COUNT(*) FROM account_profile_grants WHERE account_id = @a;", ("@a", AdminAccount)));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM account_profile_grants WHERE account_id = @a AND profile_id = @p AND is_default = 1 AND admin_enabled = 1;",
            ("@a", AdminAccount), ("@p", AdminProfile)));
        foreach (var moved in new[] { KidsProfile, GrandmaProfile })
        {
            Assert.Equal(1, Scalar(path,
                "SELECT COUNT(*) FROM account_profile_grants WHERE account_id = @a AND profile_id = @p AND is_default = 0 AND is_enabled = 1 AND admin_enabled = 0;",
                ("@a", AdminAccount), ("@p", moved)));
            Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM profiles WHERE id = @p;", ("@p", moved)));
        }

        // History keyed by profile is untouched: the Kids PIN still exists.
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM profile_credentials WHERE profile_id = @p;", ("@p", KidsProfile)));

        // The local-only account and everything that hung off it is gone...
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM account_profile_grants WHERE account_id = @a;", ("@a", LocalAccount)));
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM auth_sessions WHERE account_id = @a;", ("@a", LocalAccount)));

        // ...while rebuilding accounts did not cascade away the administrator's own rows.
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM account_credentials WHERE account_id = @a;", ("@a", AdminAccount)));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM auth_sessions WHERE account_id = @a;", ("@a", AdminAccount)));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM account_feature_grants WHERE account_id = @a;", ("@a", AdminAccount)));
    }

    [Fact]
    public void RunningTheMigrationTwiceChangesNothing()
    {
        var path = CreateLegacyDatabase(seed: raw =>
        {
            SeedAdministrator(raw);
            SeedLocalOnlyAccount(raw, KidsProfile, "Kids", GrandmaProfile, "Grandma");
        });
        using (Open(path))
        {
        }

        var grantsBefore = Scalar(path, "SELECT COUNT(*) FROM account_profile_grants;");
        var versionBefore = Scalar(path, "SELECT authorization_version FROM accounts WHERE id = @a;", ("@a", AdminAccount));

        using (var again = Open(path))
        {
            Assert.Empty(again.StartupNotes);
        }

        Assert.Equal(grantsBefore, Scalar(path, "SELECT COUNT(*) FROM account_profile_grants;"));
        Assert.Equal(versionBefore, Scalar(path, "SELECT authorization_version FROM accounts WHERE id = @a;", ("@a", AdminAccount)));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM accounts;"));
    }

    [Fact]
    public void ADatabaseWithoutLocalOnlyAccountsStillGetsARequiredEmail()
    {
        var path = CreateLegacyDatabase(seed: SeedAdministrator);

        using (var database = Open(path))
        {
            Assert.Empty(database.StartupNotes);
        }

        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM accounts;"));
        Assert.Equal(1, Scalar(path, "SELECT \"notnull\" FROM pragma_table_info('accounts') WHERE name = 'email';"));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM account_profile_grants WHERE account_id = @a;", ("@a", AdminAccount)));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM account_credentials WHERE account_id = @a;", ("@a", AdminAccount)));
    }

    [Fact]
    public void TooManyProfilesStopsStartupNamingThemAndChangesNothing()
    {
        var path = CreateLegacyDatabase(seed: raw =>
        {
            SeedAdministrator(raw);
            for (var index = 0; index < 6; index++)
            {
                var extra = Guid.NewGuid();
                Exec(raw, "INSERT INTO profiles(id, display_name, role, created_at) VALUES(@p, @n, 'StandardUser', @now);",
                    ("@p", extra), ("@n", $"Extra {index}"), ("@now", Now));
                Exec(raw, "INSERT INTO account_profile_grants(account_id, profile_id, is_default, is_enabled, admin_enabled, authorization_version, granted_at) VALUES(@a, @p, 0, 1, 0, 1, @now);",
                    ("@a", AdminAccount), ("@p", extra), ("@now", Now));
            }

            SeedLocalOnlyAccount(raw, KidsProfile, "Kids", GrandmaProfile, "Grandma");
        });

        var failure = Assert.Throws<InvalidOperationException>(() => Open(path));

        Assert.Contains("'Kids'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("'Grandma'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("tuvima-admin", failure.Message, StringComparison.Ordinal);
        Assert.Equal(2, Scalar(path, "SELECT COUNT(*) FROM accounts;"));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM pragma_table_info('accounts') WHERE name = 'is_local_only';"));
        Assert.Equal(2, Scalar(path, "SELECT COUNT(*) FROM account_profile_grants WHERE account_id = @a;", ("@a", LocalAccount)));
    }

    [Fact]
    public void WithoutAnAdministratorWithAnEmailStartupStopsAndChangesNothing()
    {
        var path = CreateLegacyDatabase(seed: raw =>
            SeedLocalOnlyAccount(raw, KidsProfile, "Kids", GrandmaProfile, "Grandma"));

        var failure = Assert.Throws<InvalidOperationException>(() => Open(path));

        Assert.Contains("'Kids'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("tuvima-admin", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM accounts;"));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM pragma_table_info('accounts') WHERE name = 'is_local_only';"));
    }

    [Fact]
    public void ADisabledAdministratorNeverReceivesProfiles()
    {
        var path = CreateLegacyDatabase(seed: raw =>
        {
            SeedAdministrator(raw);
            Exec(raw, "UPDATE accounts SET is_enabled = 0 WHERE id = @a;", ("@a", AdminAccount));
            SeedLocalOnlyAccount(raw, KidsProfile, "Kids", GrandmaProfile, "Grandma");
        });

        var failure = Assert.Throws<InvalidOperationException>(() => Open(path));

        Assert.Contains("tuvima-admin", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM account_profile_grants WHERE account_id = @a AND profile_id = @p;", ("@a", AdminAccount), ("@p", KidsProfile)));
        Assert.Equal(2, Scalar(path, "SELECT COUNT(*) FROM accounts;"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in _paths)
        {
            foreach (var file in new[] { path, $"{path}-wal", $"{path}-shm" })
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
        }
    }

    private static string Now => DateTimeOffset.UtcNow.ToString("O");

    private DatabaseConnection Open(string path)
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

    /// <summary>Builds a current data store, then puts <c>accounts</c> back into its previous shape and seeds it.</summary>
    private string CreateLegacyDatabase(Action<SqliteConnection> seed)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima-retire-local-only-{Guid.NewGuid():N}.db");
        _paths.Add(path);
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
                CREATE TABLE accounts_previous (
                    id               BLOB NOT NULL PRIMARY KEY,
                    email            TEXT,
                    normalized_email TEXT,
                    is_local_only    INTEGER NOT NULL DEFAULT 0 CHECK (is_local_only IN (0, 1)),
                    is_enabled       INTEGER NOT NULL DEFAULT 1 CHECK (is_enabled IN (0, 1)),
                    is_administrator INTEGER NOT NULL DEFAULT 0 CHECK (is_administrator IN (0, 1)),
                    authorization_version INTEGER NOT NULL DEFAULT 1 CHECK (authorization_version > 0),
                    created_at       TEXT NOT NULL,
                    updated_at       TEXT NOT NULL,
                    CHECK ((is_local_only = 1 AND email IS NULL AND normalized_email IS NULL)
                        OR (is_local_only = 0 AND email IS NOT NULL AND normalized_email IS NOT NULL))
                );
                DROP TABLE accounts;
                ALTER TABLE accounts_previous RENAME TO accounts;
                CREATE UNIQUE INDEX ux_accounts_normalized_email
                    ON accounts(normalized_email) WHERE normalized_email IS NOT NULL;
                """);
            Exec(raw, "PRAGMA foreign_keys = ON;");
            seed(raw);
        }

        SqliteConnection.ClearAllPools();
        return path;
    }

    private static void SeedAdministrator(SqliteConnection raw)
    {
        Exec(raw, "INSERT INTO profiles(id, display_name, role, created_at) VALUES(@p, 'Owner', 'Administrator', @now);",
            ("@p", AdminProfile), ("@now", Now));
        Exec(raw, """
            INSERT INTO accounts(id, email, normalized_email, is_local_only, is_enabled, is_administrator, authorization_version, created_at, updated_at)
            VALUES(@a, 'owner@example.com', 'OWNER@EXAMPLE.COM', 0, 1, 1, 1, @now, @now);
            """, ("@a", AdminAccount), ("@now", Now));
        Exec(raw, "INSERT INTO account_profile_grants(account_id, profile_id, is_default, is_enabled, admin_enabled, authorization_version, granted_at) VALUES(@a, @p, 1, 1, 1, 1, @now);",
            ("@a", AdminAccount), ("@p", AdminProfile), ("@now", Now));
        Exec(raw, "INSERT INTO account_feature_grants(account_id, feature_id, granted_at) VALUES(@a, 'read', @now);",
            ("@a", AdminAccount), ("@now", Now));
        Exec(raw, """
            INSERT INTO account_credentials(id, account_id, credential_kind, secret_hash, hash_scheme, hash_version, security_stamp, created_at, updated_at)
            VALUES(@id, @a, 'Password', 'hash', 'scheme', 1, 'stamp', @now, @now);
            """, ("@id", Guid.NewGuid()), ("@a", AdminAccount), ("@now", Now));
        InsertSession(raw, AdminAccount, AdminProfile, "admin-token");
    }

    private static void SeedLocalOnlyAccount(
        SqliteConnection raw, Guid firstProfile, string firstName, Guid secondProfile, string secondName)
    {
        Exec(raw, """
            INSERT INTO accounts(id, email, normalized_email, is_local_only, is_enabled, is_administrator, authorization_version, created_at, updated_at)
            VALUES(@a, NULL, NULL, 1, 1, 0, 1, @now, @now);
            """, ("@a", LocalAccount), ("@now", Now));
        var isDefault = 1;
        foreach (var (profile, name) in new[] { (firstProfile, firstName), (secondProfile, secondName) })
        {
            Exec(raw, "INSERT INTO profiles(id, display_name, role, created_at) VALUES(@p, @n, 'RestrictedProfile', @now);",
                ("@p", profile), ("@n", name), ("@now", Now));
            Exec(raw, "INSERT INTO account_profile_grants(account_id, profile_id, is_default, is_enabled, admin_enabled, authorization_version, granted_at) VALUES(@a, @p, @d, 1, 0, 1, @now);",
                ("@a", LocalAccount), ("@p", profile), ("@d", isDefault), ("@now", Now));
            isDefault = 0;
        }

        Exec(raw, """
            INSERT INTO profile_credentials(id, profile_id, credential_kind, secret_hash, hash_scheme, hash_version, security_stamp, created_at, updated_at)
            VALUES(@id, @p, 'ProfilePin', 'hash', 'scheme', 1, 'stamp', @now, @now);
            """, ("@id", Guid.NewGuid()), ("@p", firstProfile), ("@now", Now));
        InsertSession(raw, LocalAccount, firstProfile, "local-token");
    }

    private static void InsertSession(SqliteConnection raw, Guid account, Guid profile, string token) => Exec(raw, """
        INSERT INTO auth_sessions(id, account_id, active_profile_id, token_hash, device_id, device_name, client, authentication_method, issued_ingress, security_stamp, created_at, last_seen_at, expires_at)
        VALUES(@id, @a, @p, @t, 'device', 'Device', 'Dashboard', 'Password', 'home_network', 'stamp', @now, @now, @later);
        """, ("@id", Guid.NewGuid()), ("@a", account), ("@p", profile), ("@t", token), ("@now", Now),
            ("@later", DateTimeOffset.UtcNow.AddDays(1).ToString("O")));

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
}
