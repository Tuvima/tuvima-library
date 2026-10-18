using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// An account made on this computer has no email until it is secured, so <c>accounts.email</c> and
/// <c>normalized_email</c> allow NULL.
/// </summary>
public sealed class AccountEmailOptionalSchemaTests : IDisposable
{
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
    public void ManyAccountsMayHaveNoEmail_ButTwoMayNotShareOne()
    {
        var path = NewPath();
        using (var database = new DatabaseConnection(path))
        {
            database.InitializeSchema();
            database.RunStartupChecks();
        }

        SqliteConnection.ClearAllPools();
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var raw = new SqliteConnection($"Data Source={path};Pooling=False");
        raw.Open();
        InsertAccount(raw, null, null, now);
        InsertAccount(raw, null, null, now);
        InsertAccount(raw, "owner@example.com", "OWNER@EXAMPLE.COM", now);
        Assert.Throws<SqliteException>(() => InsertAccount(raw, "x@example.com", "OWNER@EXAMPLE.COM", now));
        Assert.Equal(3, Scalar(path, "SELECT COUNT(*) FROM accounts;"));
    }

    private static void InsertAccount(SqliteConnection raw, string? email, string? normalizedEmail, string now)
    {
        using var command = raw.CreateCommand();
        command.CommandText = "INSERT INTO accounts(id, email, normalized_email, created_at, updated_at) VALUES(@id, @email, @normalized, @now, @now);";
        command.Parameters.AddWithValue("@id", GuidSql.ToBlob(Guid.NewGuid()));
        command.Parameters.AddWithValue("@email", (object?)email ?? DBNull.Value);
        command.Parameters.AddWithValue("@normalized", (object?)normalizedEmail ?? DBNull.Value);
        command.Parameters.AddWithValue("@now", now);
        command.ExecuteNonQuery();
    }

    private string NewPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima-account-email-{Guid.NewGuid():N}.db");
        _paths.Add(path);
        return path;
    }

    private static long NotNull(string path, string column) =>
        Scalar(path, $"SELECT \"notnull\" FROM pragma_table_info('accounts') WHERE name = '{column}';");

    private static long Scalar(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
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
