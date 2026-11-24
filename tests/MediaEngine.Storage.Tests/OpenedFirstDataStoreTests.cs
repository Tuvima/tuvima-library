using Dapper;
using MediaEngine.Storage;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

public sealed class OpenedFirstDataStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"opened-first-{Guid.NewGuid():N}.db");

    [Fact]
    public void ADataStoreOpenedBeforeItsSchemaIsBuilt_StillEndsUpFullyUpgraded()
    {
        using var db = new DatabaseConnection(_path);
        db.Open();
        db.InitializeSchema();
        db.RunStartupChecks();

        using var connection = db.CreateConnection();
        var recorded = connection.ExecuteScalar<long>("SELECT COUNT(*) FROM data_store_upgrades");
        var tables = connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='table'");
        Assert.True(recorded > 0);
        Assert.True(tables > 20);
    }

    public void Dispose() => TestTemp.DeleteDatabase(_path);
}
