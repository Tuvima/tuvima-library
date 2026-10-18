using System.Diagnostics;
using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Benchmarks.Queries;
using MediaEngine.Storage.Benchmarks.Seeding;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaEngine.Storage.Benchmarks.Concurrency;

/// <summary>
/// One scenario/stack run, executed in its own process so peak working set and GC counts belong to that run alone.
///
/// Scenario A (shared writer): one background writer always uses the production Dapper write path
/// (<c>IDatabaseConnection.ExecuteWriteAsync</c>: global write lock, one connection, one transaction); the readers use the
/// stack under test. Scenario B (own writer): the writer also uses the stack under test.
///
/// Per transaction the writer appends 50 metadata_claims and updates 20 canonical_values, as fast as it can.
/// Four reader tasks loop Q2 (claims read) and Q4 (library items page, media type filter) on the stack under test.
/// </summary>
internal sealed class ConcurrencyRun
{
    public const int ReaderTasks = 4;
    public const int ClaimsPerTransaction = 50;
    public const int UpdatesPerTransaction = 20;

    private const string InsertClaimSql = """
        INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at, is_user_locked, is_current)
        VALUES (@id, @entityId, @providerId, 'bench_note', @value, 0.5, @claimedAt, 0, 1);
        """;

    private const string UpdateValueSql = """
        UPDATE canonical_values SET value = @value, last_scored_at = @scoredAt
        WHERE entity_id = @entityId AND key = 'title';
        """;

    private readonly BenchContext _context;
    private readonly bool _useEf;
    private readonly bool _writerUsesEf;
    private readonly TimeSpan _duration;
    private readonly Stopwatch _clock = new();

    private int _writerTransactions;
    private int _writerBusy;
    private int _readerBusy;
    private int _otherErrors;
    private readonly List<string> _errorSamples = [];
    private readonly object _errorGate = new();

    public ConcurrencyRun(BenchContext context, string scenario, string stack, TimeSpan duration)
    {
        _context = context;
        Scenario = scenario;
        Stack = stack;
        _useEf = stack == "ef";
        _writerUsesEf = scenario == "B" && _useEf;
        _duration = duration;
    }

    public string Scenario { get; }

    public string Stack { get; }

    public async Task<ConcurrencyResult> ExecuteAsync()
    {
        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        var allocatedBefore = GC.GetTotalAllocatedBytes();

        _clock.Start();
        var latencies = Enumerable.Range(0, ReaderTasks).Select(_ => new Dictionary<string, List<double>>()).ToArray();
        var tasks = new List<Task> { Task.Run(WriterLoopAsync) };
        for (var reader = 0; reader < ReaderTasks; reader++)
        {
            var index = reader;
            tasks.Add(Task.Run(() => ReaderLoopAsync(index, latencies[index])));
        }

        await Task.WhenAll(tasks);
        _clock.Stop();

        var merged = new Dictionary<string, List<double>>();
        foreach (var perReader in latencies)
        {
            foreach (var (operation, values) in perReader)
            {
                if (!merged.TryGetValue(operation, out var list))
                {
                    merged[operation] = list = [];
                }

                list.AddRange(values);
            }
        }

        var walPath = _context.DatabasePath + "-wal";
        var walMiB = File.Exists(walPath) ? new FileInfo(walPath).Length / 1024.0 / 1024.0 : 0;
        using var process = Process.GetCurrentProcess();
        return new ConcurrencyResult(
            Scenario,
            Stack,
            _clock.Elapsed.TotalSeconds,
            merged.OrderBy(pair => pair.Key).Select(pair => Summarise(pair.Key, pair.Value)).ToList(),
            _writerTransactions,
            _writerBusy,
            _readerBusy,
            _otherErrors,
            _errorSamples,
            process.PeakWorkingSet64 / 1024.0 / 1024.0,
            GC.CollectionCount(0) - gen0,
            GC.CollectionCount(1) - gen1,
            GC.CollectionCount(2) - gen2,
            (GC.GetTotalAllocatedBytes() - allocatedBefore) / 1024.0 / 1024.0,
            walMiB);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Readers
    // ---------------------------------------------------------------------------------------------------------
    private async Task ReaderLoopAsync(int index, Dictionary<string, List<double>> latencies)
    {
        var turn = index; // stagger so Q2 and Q4 both start at once
        while (_clock.Elapsed < _duration)
        {
            var operation = turn++ % 2 == 0 ? "Q2 claims read" : "Q4 items page (media type)";
            var started = Stopwatch.GetTimestamp();
            try
            {
                if (operation.StartsWith("Q2", StringComparison.Ordinal))
                {
                    await (_useEf
                        ? Q2MetadataClaims.EfLinq(_context, _context.Workload.Q2Ids)
                        : Q2MetadataClaims.Dapper(_context, _context.Workload.Q2Ids));
                }
                else
                {
                    await (_useEf
                        ? Q4LibraryItems.EfRaw(_context, _context.Workload.ItemsMediaType)
                        : Q4LibraryItems.Dapper(_context, _context.Workload.ItemsMediaType));
                }

                if (!latencies.TryGetValue(operation, out var list))
                {
                    latencies[operation] = list = [];
                }

                list.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            catch (Exception exception)
            {
                // Not swallowed: every failure is classified, counted and reported in the result (busy/locked counts
                // and the first error messages are printed by the parent, and any non-busy error fails the run).
                RecordError(exception, isWriter: false);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Writer
    // ---------------------------------------------------------------------------------------------------------
    private async Task WriterLoopAsync()
    {
        long batch = 0;
        while (_clock.Elapsed < _duration)
        {
            try
            {
                if (_writerUsesEf)
                {
                    await EfWriteAsync(batch);
                }
                else
                {
                    await DapperWriteAsync(batch);
                }

                Interlocked.Increment(ref _writerTransactions);
            }
            catch (Exception exception)
            {
                RecordError(exception, isWriter: true); // see ReaderLoopAsync: counted and reported, never silent
            }

            batch++;
        }
    }

    private Task DapperWriteAsync(long batch)
    {
        var now = DateTimeOffset.UtcNow;
        return _context.Database.ExecuteWriteAsync((connection, transaction, _) =>
        {
            connection.Execute(
                InsertClaimSql,
                Enumerable.Range(0, ClaimsPerTransaction).Select(i => new
                {
                    id = Guid.NewGuid(),
                    entityId = AssetId(batch * ClaimsPerTransaction + i),
                    providerId = WellKnownProviders.LocalProcessor,
                    value = $"note {batch}.{i}",
                    claimedAt = now,
                }),
                transaction);
            connection.Execute(
                UpdateValueSql,
                Enumerable.Range(0, UpdatesPerTransaction).Select(i => new
                {
                    entityId = AssetId(batch * UpdatesPerTransaction + i),
                    value = $"Updated title {batch}.{i}",
                    scoredAt = now,
                }),
                transaction);
        });
    }

    /// <summary>
    /// EF write path: a pooled, change-tracking context on a production connection, one transaction and one
    /// SaveChanges per batch. The global write lock is taken exactly as <c>ExecuteWriteAsync</c> takes it, so write
    /// serialisation is identical in both stacks and only the data-access technique differs.
    /// </summary>
    private async Task EfWriteAsync(long batch)
    {
        var now = DateTimeOffset.UtcNow;
        await _context.Database.AcquireWriteLockAsync();
        try
        {
            using var lease = _context.Ef.RentTracking();
            try
            {
                var context = lease.Context;
                await using var transaction = await context.Database.BeginTransactionAsync();

                for (var i = 0; i < ClaimsPerTransaction; i++)
                {
                    context.MetadataClaims.Add(new MetadataClaim
                    {
                        Id = Guid.NewGuid(),
                        EntityId = AssetId(batch * ClaimsPerTransaction + i),
                        ProviderId = WellKnownProviders.LocalProcessor,
                        ClaimKey = "bench_note",
                        ClaimValue = $"note {batch}.{i}",
                        Confidence = 0.5,
                        ClaimedAt = now,
                        IsUserLocked = false,
                        IsCurrent = true,
                    });
                }

                var entityIds = Enumerable.Range(0, UpdatesPerTransaction)
                    .Select(i => AssetId(batch * UpdatesPerTransaction + i))
                    .ToList();
                var values = await context.CanonicalValues
                    .Where(value => entityIds.Contains(value.EntityId) && value.Key == "title")
                    .ToListAsync();
                foreach (var value in values)
                {
                    value.Value = $"Updated title {batch}.{entityIds.IndexOf(value.EntityId)}";
                    value.LastScoredAt = now;
                }

                await context.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            finally
            {
                // The pool resets state on return; restore the shared default explicitly as well.
                lease.Context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
            }
        }
        finally
        {
            _context.Database.ReleaseWriteLock();
        }
    }

    private Guid AssetId(long position) =>
        SeedIds.Id(SeedIds.Asset, 1 + position % _context.Info.Works);

    // ---------------------------------------------------------------------------------------------------------
    // Bookkeeping
    // ---------------------------------------------------------------------------------------------------------
    private void RecordError(Exception exception, bool isWriter)
    {
        if (IsBusyOrLocked(exception))
        {
            if (isWriter)
            {
                Interlocked.Increment(ref _writerBusy);
            }
            else
            {
                Interlocked.Increment(ref _readerBusy);
            }
        }
        else
        {
            Interlocked.Increment(ref _otherErrors);
        }

        lock (_errorGate)
        {
            if (_errorSamples.Count < 5)
            {
                _errorSamples.Add($"{(isWriter ? "writer" : "reader")}: {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    /// <summary>SQLITE_BUSY (5) or SQLITE_LOCKED (6), also when wrapped by EF.</summary>
    private static bool IsBusyOrLocked(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException { SqliteErrorCode: 5 or 6 })
            {
                return true;
            }
        }

        return false;
    }

    private static OperationStats Summarise(string operation, List<double> values)
    {
        values.Sort();
        return new OperationStats(
            operation,
            values.Count,
            Percentile(values, 0.50),
            Percentile(values, 0.95),
            Percentile(values, 0.99),
            values.Count == 0 ? 0 : values[^1]);
    }

    /// <summary>Nearest-rank percentile of an ascending list.</summary>
    private static double Percentile(List<double> sorted, double fraction)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var rank = (int)Math.Ceiling(fraction * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }
}
