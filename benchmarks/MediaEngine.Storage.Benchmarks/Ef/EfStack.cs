using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Storage.Benchmarks.Ef;

/// <summary>
/// The EF side of the comparison. Every operation takes its own connection from the production
/// <see cref="IDatabaseConnection.CreateConnection"/> (so EF gets exactly the production pragmas: WAL,
/// synchronous=NORMAL, busy_timeout, cache and mmap) and its own context, then disposes both, as a request does.
///
/// EF options set, and nothing else: SQLite provider, <c>QueryTrackingBehavior.NoTracking</c> as the default.
/// Logging off, no sensitive-data logging, thread-safety checks left at their default (on).
/// </summary>
internal sealed class EfStack : IDisposable
{
    private readonly IDatabaseConnection _database;
    private readonly DbContextOptions<BenchDbContext> _options;
    private readonly ServiceProvider _services;
    private readonly IDbContextFactory<BenchDbContext> _pool;

    public EfStack(IDatabaseConnection database)
    {
        _database = database;

        var optionsBuilder = new DbContextOptionsBuilder<BenchDbContext>();
        Configure(optionsBuilder);
        _options = optionsBuilder.Options;

        // The pooled factory is registered the way an Engine host would register it.
        _services = new ServiceCollection()
            .AddPooledDbContextFactory<BenchDbContext>(Configure, poolSize: 256)
            .BuildServiceProvider();
        _pool = _services.GetRequiredService<IDbContextFactory<BenchDbContext>>();
    }

    /// <summary>A context from the pool, bound to a fresh production connection.</summary>
    public EfLease Rent()
    {
        var connection = _database.CreateConnection();
        var context = _pool.CreateDbContext();
        context.Database.SetDbConnection(connection);
        return new EfLease(context, connection);
    }

    /// <summary>A brand-new, non-pooled context bound to a fresh production connection (the "cold" request cost).</summary>
    public EfLease RentFresh()
    {
        var connection = _database.CreateConnection();
        var context = new BenchDbContext(_options);
        context.Database.SetDbConnection(connection);
        return new EfLease(context, connection);
    }

    /// <summary>A pooled context with change tracking switched on, for the EF write path.</summary>
    public EfLease RentTracking()
    {
        var lease = Rent();
        lease.Context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
        return lease;
    }

    public void Dispose() => _services.Dispose();

    private static void Configure(DbContextOptionsBuilder builder) =>
        builder
            // The data source is never used: each lease swaps in a connection from CreateConnection(). An empty
            // in-memory database makes any accidental use fail loudly ("no such table") instead of reading a file.
            .UseSqlite("Data Source=:memory:")
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
}

/// <summary>Context plus connection for one operation. Disposing returns the context, then closes the connection.</summary>
internal readonly struct EfLease(BenchDbContext context, SqliteConnection connection) : IDisposable
{
    public BenchDbContext Context { get; } = context;

    public SqliteConnection Connection { get; } = connection;

    public void Dispose()
    {
        Context.Dispose();
        Connection.Dispose();
    }
}
