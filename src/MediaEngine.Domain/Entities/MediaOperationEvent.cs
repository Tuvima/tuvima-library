namespace MediaEngine.Domain.Entities;

public sealed class MediaOperationEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid OperationId { get; init; }
    public Guid? EntityId { get; init; }
    public string EventType { get; init; } = "";
    public string? OldStatus { get; init; }
    public string? NewStatus { get; init; }
    public string? OldStage { get; init; }
    public string? NewStage { get; init; }
    public string? Message { get; init; }
    public string? DetailJson { get; init; }
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}
