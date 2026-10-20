namespace MediaEngine.Domain.Entities;

/// <summary>
/// A single entry in the system activity ledger.
///
/// Records every significant action that occurs in the Engine: file ingestion,
/// metadata hydration, hash verification, path updates, sync completions, and
/// library crawl events.
///
/// Designed for non-blocking, async-first persistence via
/// <see cref="Contracts.ISystemActivityRepository"/>.
/// </summary>
public sealed class SystemActivityEntry
{
    /// <summary>Database-assigned auto-increment row ID.</summary>
    public long Id { get; set; }

    /// <summary>When the action occurred (UTC).</summary>
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Machine-readable action type.
    /// Use <see cref="Constants.SystemActionType"/> constants.
    /// </summary>
    public required string ActionType { get; init; }

    /// <summary>Human-readable collection display name for context (nullable — not all actions are collection-scoped).</summary>
    public string? CollectionName { get; init; }

    /// <summary>The UUID of the affected entity (nullable).</summary>
    public Guid? EntityId { get; init; }

    /// <summary>The kind of entity affected: Hub, Work, MediaAsset, Person (nullable).</summary>
    public string? EntityType { get; init; }

    /// <summary>The profile that triggered the action; null for automated actions.</summary>
    public Guid? ProfileId { get; init; }

    /// <summary>Compact JSON snippet describing what changed (nullable).</summary>
    public string? ChangesJson { get; init; }

    /// <summary>Human-readable one-line summary of the action.</summary>
    public string? Detail { get; init; }
}
