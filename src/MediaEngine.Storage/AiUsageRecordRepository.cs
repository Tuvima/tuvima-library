using System.Globalization;
using Dapper;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>SQLite implementation of <see cref="IAiUsageRecordRepository"/>, backed by <c>ai_usage_records</c>.</summary>
public sealed class AiUsageRecordRepository : IAiUsageRecordRepository
{
    private const string Columns =
        "id AS Id, started_at AS StartedAt, caller_kind AS CallerKind, caller_id AS CallerId, ability AS Ability, "
        + "engine AS Engine, wait_ms AS WaitMs, duration_ms AS DurationMs, outcome AS Outcome, message AS Message";

    private readonly IDatabaseConnection _db;

    public AiUsageRecordRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task AddAsync(AiUsageRecord record, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.CallerId);

        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO ai_usage_records (
                id, started_at, caller_kind, caller_id, ability, engine, wait_ms, duration_ms, outcome, message)
            VALUES (
                @Id, @StartedAt, @CallerKind, @CallerId, @Ability, @Engine, @WaitMs, @DurationMs, @Outcome, @Message);
            """,
            new
            {
                record.Id,
                StartedAt = Format(record.StartedAt),
                CallerKind = record.CallerKind.ToKey(),
                record.CallerId,
                Ability = record.Ability.ToKey(),
                Engine = record.Engine.ToKey(),
                WaitMs = Math.Max(record.WaitMs, 0),
                DurationMs = Math.Max(record.DurationMs, 0),
                Outcome = record.Outcome.ToKey(),
                record.Message,
            });
        return Task.CompletedTask;
    }

    public Task<AiUsageTotals> GetTotalsSinceAsync(AiCallerKind kind, string callerId, DateTimeOffset since, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(callerId);

        using var conn = _db.CreateConnection();
        var row = conn.QuerySingle<(long Requests, long BusyMs)>("""
            SELECT COUNT(*) AS Requests, COALESCE(SUM(duration_ms), 0) AS BusyMs
            FROM ai_usage_records
            WHERE caller_kind = @kind AND caller_id = @callerId AND started_at >= @since
              AND outcome NOT IN (@refused, @unavailable);
            """,
            new
            {
                kind = kind.ToKey(),
                callerId,
                since = Format(since),
                refused = AiRequestStatus.RefusedOverBudget.ToKey(),
                unavailable = AiRequestStatus.Unavailable.ToKey(),
            });
        return Task.FromResult(new AiUsageTotals((int)row.Requests, row.BusyMs));
    }

    public Task<IReadOnlyList<AiUsageRecord>> ListRecentAsync(
        int limit,
        int skip = 0,
        AiCallerKind? kind = null,
        string? callerId = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var rows = conn.Query<Row>($"""
            SELECT {Columns}
            FROM ai_usage_records
            WHERE (@kind IS NULL OR caller_kind = @kind) AND (@callerId IS NULL OR caller_id = @callerId)
            ORDER BY started_at DESC, rowid DESC
            LIMIT @limit OFFSET @skip;
            """,
            new { kind = kind?.ToKey(), callerId, limit = Math.Clamp(limit, 0, 1000), skip = Math.Max(skip, 0) });
        return Task.FromResult<IReadOnlyList<AiUsageRecord>>(rows.Select(row => row.ToRecord()).ToList());
    }

    public Task<IReadOnlyList<AiUsageSummaryRow>> SummariseSinceAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var rows = conn.Query<SummaryRow>("""
            SELECT caller_kind AS CallerKind, caller_id AS CallerId, ability AS Ability,
                   COUNT(*) AS Requests,
                   SUM(CASE WHEN outcome = @succeeded THEN 1 ELSE 0 END) AS Succeeded,
                   COALESCE(SUM(duration_ms), 0) AS BusyMs,
                   MAX(started_at) AS LastUsedAt
            FROM ai_usage_records
            WHERE started_at >= @since
            GROUP BY caller_kind, caller_id, ability
            ORDER BY Requests DESC, CallerId, Ability;
            """,
            new { since = Format(since), succeeded = AiRequestStatus.Succeeded.ToKey() });
        return Task.FromResult<IReadOnlyList<AiUsageSummaryRow>>(rows.Select(row => row.ToSummary()).ToList());
    }

    public Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var deleted = conn.Execute(
            "DELETE FROM ai_usage_records WHERE started_at < @cutoff;",
            new { cutoff = Format(now.AddDays(-IAiUsageRecordRepository.RetentionDays)) });
        return Task.FromResult(deleted);
    }

    private static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal);

    private static AiCallerKind ParseKind(string key) =>
        AiVocabulary.TryParseCallerKind(key, out var kind)
            ? kind
            : throw new InvalidDataException($"Unknown AI caller kind '{key}' in ai_usage_records.");

    private static AiAbility ParseAbility(string key) =>
        AiVocabulary.TryParseAbility(key, out var ability)
            ? ability
            : throw new InvalidDataException($"Unknown AI ability '{key}' in ai_usage_records.");

    private static AiEngine ParseEngine(string key) =>
        ParseKey<AiEngine>(key, AiVocabulary.ToKey, "engine");

    private static AiRequestStatus ParseOutcome(string key) =>
        ParseKey<AiRequestStatus>(key, AiVocabulary.ToKey, "outcome");

    private static T ParseKey<T>(string key, Func<T, string> toKey, string what) where T : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (string.Equals(toKey(candidate), key, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        throw new InvalidDataException($"Unknown AI {what} '{key}' in ai_usage_records.");
    }

    private sealed class Row
    {
        public Guid Id { get; set; }
        public string StartedAt { get; set; } = "";
        public string CallerKind { get; set; } = "";
        public string CallerId { get; set; } = "";
        public string Ability { get; set; } = "";
        public string Engine { get; set; } = "";
        public int WaitMs { get; set; }
        public int DurationMs { get; set; }
        public string Outcome { get; set; } = "";
        public string? Message { get; set; }

        public AiUsageRecord ToRecord() => new(
            Id,
            Parse(StartedAt),
            ParseKind(CallerKind),
            CallerId,
            ParseAbility(Ability),
            ParseEngine(Engine),
            WaitMs,
            DurationMs,
            ParseOutcome(Outcome),
            Message);
    }

    private sealed class SummaryRow
    {
        public string CallerKind { get; set; } = "";
        public string CallerId { get; set; } = "";
        public string Ability { get; set; } = "";
        public long Requests { get; set; }
        public long Succeeded { get; set; }
        public long BusyMs { get; set; }
        public string LastUsedAt { get; set; } = "";

        public AiUsageSummaryRow ToSummary() => new(
            ParseKind(CallerKind),
            CallerId,
            ParseAbility(Ability),
            (int)Requests,
            (int)Succeeded,
            BusyMs,
            Parse(LastUsedAt));
    }
}
