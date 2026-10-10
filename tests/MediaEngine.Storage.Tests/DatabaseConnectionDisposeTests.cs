namespace MediaEngine.Storage.Tests;

/// <summary>
/// Disposing a <see cref="DatabaseConnection"/> must release pooled operation connections, otherwise Windows keeps the
/// data file locked and test (or host) cleanup cannot delete it. Deliberately does not call ClearPool itself.
/// </summary>
public sealed class DatabaseConnectionDisposeTests
{
    [Fact]
    public void Dispose_ReleasesPooledConnections_SoFilesCanBeDeleted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima_dispose_{Guid.NewGuid():N}.db");
        var db = new DatabaseConnection(path);
        db.InitializeSchema();
        db.RunStartupChecks();
        using (var conn = db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            cmd.ExecuteScalar();
        }

        db.Dispose();

        File.Delete(path);
        File.Delete(path + "-wal");
        File.Delete(path + "-shm");
        Assert.False(File.Exists(path));
    }
}
