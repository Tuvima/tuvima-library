using MediaEngine.Storage.Benchmarks.Ef;
using MediaEngine.Storage.Benchmarks.Seeding;

namespace MediaEngine.Storage.Benchmarks;

/// <summary>
/// One open benchmark target: the production <see cref="DatabaseConnection"/> over the seeded file, the EF stack that
/// borrows its connections, and the fixed workload derived from the dataset description.
/// </summary>
internal sealed class BenchContext : IDisposable
{
    private BenchContext(string databasePath, DatasetInfo info, DatabaseConnection database)
    {
        DatabasePath = databasePath;
        Info = info;
        Database = database;
        Ef = new EfStack(database);
        Workload = new Workload(info);
    }

    public string DatabasePath { get; }

    public DatasetInfo Info { get; }

    public DatabaseConnection Database { get; }

    public EfStack Ef { get; }

    public Workload Workload { get; }

    /// <summary>Opens an already-seeded file. Never creates or migrates it.</summary>
    public static BenchContext Open(string databasePath)
    {
        databasePath = Path.GetFullPath(databasePath);
        if (!File.Exists(databasePath))
        {
            throw new InvalidOperationException($"'{databasePath}' does not exist. Run 'seed --db <path>' first.");
        }

        return new BenchContext(databasePath, DatasetInfo.Load(databasePath), new DatabaseConnection(databasePath));
    }

    public void Dispose()
    {
        Ef.Dispose();
        Database.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}
