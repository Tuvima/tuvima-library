using Dapper;
using Xunit.Abstractions;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// The readiness service asks "what changed since last time" every couple of seconds. These check the questions use
/// the indexes added for them instead of reading whole tables.
/// </summary>
public sealed class WorkReadinessQueryPlanTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly ITestOutputHelper _output;

    public WorkReadinessQueryPlanTests(ITestOutputHelper output)
    {
        _output = output;
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima-work-readiness-plan-{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        Seed();
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch (Exception) { /* temp database cleanup is best effort */ }
        try { File.Delete(_dbPath); } catch (IOException) { /* temp database cleanup is best effort */ }
    }

    [Fact]
    public void NewestJobTime_AndTheJobsLookUp_UseTheUpdatedAtIndex()
    {
        var newest = Plan(WorkReadinessRepository.NewestJobTimeSql);
        var touched = Plan(WorkReadinessRepository.TouchedByJobsSql);

        Assert.Contains(newest, line => line.Contains("idx_identity_jobs_updated_at", StringComparison.Ordinal));
        Assert.Contains(touched, line => line.Contains("idx_identity_jobs_updated_at", StringComparison.Ordinal));
    }

    [Fact]
    public void NewestArtworkTime_AndTheArtworkLookUp_UseThePartialScoringIndex()
    {
        var newest = Plan(WorkReadinessRepository.NewestArtworkTimeSql);
        var touched = Plan(WorkReadinessRepository.TouchedByArtworkSql);

        Assert.Contains(newest, line => line.Contains("idx_canonical_values_artwork_scored", StringComparison.Ordinal));
        Assert.Contains(touched, line => line.Contains("idx_canonical_values_artwork_scored", StringComparison.Ordinal));
    }

    private List<string> Plan(string sql)
    {
        using var conn = _db.CreateConnection();
        using var command = conn.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        command.Parameters.AddWithValue("@since", "2026-06-01T00:00:00.0000000+00:00");
        command.Parameters.AddWithValue("@max", "2026-12-31T00:00:00.0000000+00:00");
        var lines = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            lines.Add(reader.GetString(3));
        }

        _output.WriteLine(string.Join(Environment.NewLine, lines));
        return lines;
    }

    private void Seed()
    {
        using var conn = _db.CreateConnection();
        conn.Execute("""
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 20000)
            INSERT INTO identity_jobs (id, entity_id, entity_type, media_type, state, created_at, updated_at)
            SELECT randomblob(16), randomblob(16), 'MediaAsset', 'Books', 'Ready',
                   strftime('%Y-%m-%dT%H:%M:%f0000+00:00', '2026-01-01', '+' || i || ' seconds'),
                   strftime('%Y-%m-%dT%H:%M:%f0000+00:00', '2026-01-01', '+' || i || ' seconds')
            FROM n;

            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 60000)
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            SELECT randomblob(16),
                   CASE i % 6 WHEN 0 THEN 'cover_state' WHEN 1 THEN 'cover_url' WHEN 2 THEN 'artwork_settled_at'
                              WHEN 3 THEN 'title' WHEN 4 THEN 'genre' ELSE 'description' END,
                   'x',
                   strftime('%Y-%m-%dT%H:%M:%f0000+00:00', '2026-01-01', '+' || i || ' seconds')
            FROM n;

            ANALYZE;
            """);
    }
}
