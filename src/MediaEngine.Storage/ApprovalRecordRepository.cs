using System.Globalization;
using Dapper;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>SQLite implementation of <see cref="IApprovalRecordRepository"/>, backed by <c>approval_records</c>. Insert and read only.</summary>
public sealed class ApprovalRecordRepository : IApprovalRecordRepository
{
    private const string Columns =
        "id AS Id, item_kind AS ItemKind, item_id AS ItemId, statement_hash AS StatementHash, statement_json AS StatementJson, "
        + "approved_by_account_id AS ApprovedByAccountId, approved_by_profile_id AS ApprovedByProfileId, "
        + "approved_by_application_id AS ApprovedByApplicationId, approved_by_name AS ApprovedByName, approved_at AS ApprovedAt";

    private readonly IDatabaseConnection _db;

    public ApprovalRecordRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task AddAsync(ApprovalRecord record, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.ItemKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.ItemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.StatementHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.StatementJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.ApprovedByName);

        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO approval_records (
                id, item_kind, item_id, statement_hash, statement_json,
                approved_by_account_id, approved_by_profile_id, approved_by_application_id, approved_by_name, approved_at)
            VALUES (
                @Id, @ItemKind, @ItemId, @StatementHash, @StatementJson,
                @ApprovedByAccountId, @ApprovedByProfileId, @ApprovedByApplicationId, @ApprovedByName, @ApprovedAt);
            """,
            new
            {
                record.Id,
                record.ItemKind,
                record.ItemId,
                record.StatementHash,
                record.StatementJson,
                record.ApprovedByAccountId,
                record.ApprovedByProfileId,
                record.ApprovedByApplicationId,
                record.ApprovedByName,
                ApprovedAt = Format(record.ApprovedAt),
            });
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ApprovalRecord>> ListAsync(string itemKind, string itemId, int limit = 50, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var rows = conn.Query<Row>($"""
            SELECT {Columns}
            FROM approval_records
            WHERE item_kind = @itemKind AND item_id = @itemId
            ORDER BY approved_at DESC, rowid DESC
            LIMIT @limit;
            """,
            new { itemKind, itemId, limit = Math.Clamp(limit, 1, 500) });
        return Task.FromResult<IReadOnlyList<ApprovalRecord>>(rows.Select(row => row.ToRecord()).ToList());
    }

    public Task<bool> ExistsAsync(string itemKind, string itemId, string statementHash, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var found = conn.ExecuteScalar<long>("""
            SELECT EXISTS (
                SELECT 1 FROM approval_records
                WHERE item_kind = @itemKind AND item_id = @itemId AND statement_hash = @statementHash);
            """,
            new { itemKind, itemId, statementHash });
        return Task.FromResult(found != 0);
    }

    private static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal);

    private sealed class Row
    {
        public Guid Id { get; set; }
        public string ItemKind { get; set; } = "";
        public string ItemId { get; set; } = "";
        public string StatementHash { get; set; } = "";
        public string StatementJson { get; set; } = "";
        public Guid? ApprovedByAccountId { get; set; }
        public Guid? ApprovedByProfileId { get; set; }
        public Guid? ApprovedByApplicationId { get; set; }
        public string ApprovedByName { get; set; } = "";
        public string ApprovedAt { get; set; } = "";

        public ApprovalRecord ToRecord() => new(
            Id,
            ItemKind,
            ItemId,
            StatementHash,
            StatementJson,
            ApprovedByAccountId,
            ApprovedByProfileId,
            ApprovedByApplicationId,
            ApprovedByName,
            Parse(ApprovedAt));
    }
}
