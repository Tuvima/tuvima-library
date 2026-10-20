namespace MediaEngine.Contracts.Timeline;

/// <summary>A public timeline projection of an internal entity event.</summary>
public sealed class EntityTimelineEventDto
{
    public Guid Id { get; set; }
    public Guid EntityId { get; set; }
    public string EntityType { get; set; } = "";
    public string EventType { get; set; } = "";
    public int? Stage { get; set; }
    public string Trigger { get; set; } = "";
    public string? ProviderId { get; set; }
    public string? ProviderName { get; set; }
    public string? BridgeIdType { get; set; }
    public string? BridgeIdValue { get; set; }
    public string? ResolvedQid { get; set; }
    public double? Confidence { get; set; }
    public double? ScoreTitle { get; set; }
    public double? ScoreAuthor { get; set; }
    public double? ScoreYear { get; set; }
    public double? ScoreFormat { get; set; }
    public double? ScoreCrossField { get; set; }
    public double? ScoreCoverArt { get; set; }
    public double? ScoreComposite { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string? Detail { get; set; }
}

/// <summary>A public timeline projection of an internal field-level change.</summary>
public sealed class EntityTimelineFieldChangeDto
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public Guid EntityId { get; set; }
    public string Field { get; set; } = "";
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? OldProviderId { get; set; }
    public string? NewProviderId { get; set; }
    public double? Confidence { get; set; }
    public bool IsFileOriginal { get; set; }
}
