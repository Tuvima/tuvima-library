using Dapper;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MediaEngine.Api.Services.HealthChecks;

public sealed class SqliteHealthCheck(IDatabaseConnection db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var conn = db.CreateConnection();
            var integrity = conn.ExecuteScalar<string>("PRAGMA quick_check;");
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return HealthCheckResult.Unhealthy(
                    $"SQLite quick_check failed: {integrity}",
                    data: DatabaseData(("integrity", integrity ?? "unknown")));
            }

            var foreignKeyFailures = conn.Query("PRAGMA foreign_key_check;").Take(1).Any();
            if (foreignKeyFailures)
            {
                return HealthCheckResult.Unhealthy(
                    "SQLite foreign_key_check found an integrity error.",
                    data: DatabaseData(("integrity", "ok"), ("foreign_keys", "failed")));
            }

            // The probe is a TEMP table so it never bumps the main schema version. A main-schema
            // CREATE/DROP made concurrent readers of the FTS5 search_index fail with SQLite error 17
            // ("vtable constructor failed"), which reported the whole data store as unhealthy.
            var probeTable = $"__tuvima_readiness_write_probe_{Guid.NewGuid():N}";
            await db.ExecuteWriteAsync((writeConnection, transaction, innerCt) =>
            {
                innerCt.ThrowIfCancellationRequested();
                writeConnection.Execute($"CREATE TEMP TABLE [{probeTable}] (id INTEGER);", transaction: transaction);
                writeConnection.Execute($"DROP TABLE temp.[{probeTable}];", transaction: transaction);
            }, cancellationToken).ConfigureAwait(false);

            return HealthCheckResult.Healthy(
                "SQLite integrity, foreign keys, and write access are ready.",
                DatabaseData(("integrity", "ok"), ("foreign_keys", "ok"), ("write_access", "ok")));
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                "SQLite database integrity or write access is not ready.",
                ex,
                DatabaseData(("error", ex.Message)));
        }
    }

    private static Dictionary<string, object> DatabaseData(params (string Key, object Value)[] values)
    {
        var data = new Dictionary<string, object> { ["category"] = "database", ["required"] = true };
        foreach (var (key, value) in values)
        {
            data[key] = value;
        }

        return data;
    }
}
