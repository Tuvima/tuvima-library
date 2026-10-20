namespace MediaEngine.Storage.Contracts;

/// <summary>The kinds of item that need an approval before they are switched on. Stored as these exact words.</summary>
public static class ApprovalItemKinds
{
    public const string Plugin = "plugin";
    public const string AiFeature = "ai_feature";
    public const string ApplicationCredential = "application_credential";
    public const string Application = "application";
    public const string Webhook = "webhook";
}

/// <summary>
/// One confirmation, as stored in <c>approval_records</c>.
/// </summary>
/// <param name="ItemKind">One of <see cref="ApprovalItemKinds"/>.</param>
/// <param name="ItemId">Plugin id, AI feature key, credential id or webhook id.</param>
/// <param name="StatementHash">SHA-256 (lower-case hex) of the statement the person was shown.</param>
/// <param name="StatementJson">The statement exactly as shown. Never holds a secret value.</param>
/// <param name="ApprovedByAccountId">The account that confirmed; null when an application confirmed.</param>
/// <param name="ApprovedByProfileId">The profile that was active when a person confirmed.</param>
/// <param name="ApprovedByApplicationId">The application that confirmed; null when a person confirmed.</param>
/// <param name="ApprovedByName">Profile display name (or application name) at the time.</param>
public sealed record ApprovalRecord(
    Guid Id,
    string ItemKind,
    string ItemId,
    string StatementHash,
    string StatementJson,
    Guid? ApprovedByAccountId,
    Guid? ApprovedByProfileId,
    Guid? ApprovedByApplicationId,
    string ApprovedByName,
    DateTimeOffset ApprovedAt);

/// <summary>
/// Append-only history of approvals. There is deliberately no way to change or remove a record.
/// </summary>
public interface IApprovalRecordRepository
{
    /// <summary>Adds one record.</summary>
    Task AddAsync(ApprovalRecord record, CancellationToken ct = default);

    /// <summary>The records for one item, newest first.</summary>
    Task<IReadOnlyList<ApprovalRecord>> ListAsync(string itemKind, string itemId, int limit = 50, CancellationToken ct = default);

    /// <summary>True when a record for this item holds exactly this statement hash.</summary>
    Task<bool> ExistsAsync(string itemKind, string itemId, string statementHash, CancellationToken ct = default);
}
