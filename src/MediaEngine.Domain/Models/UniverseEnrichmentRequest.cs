namespace MediaEngine.Domain.Models;

/// <summary>
/// Inline Stage 3 request queued after the quick hydration pass finishes for a media file.
/// </summary>
public sealed record UniverseEnrichmentRequest(
    Guid JobId,
    Guid EntityId,
    string WorkQid,
    string MediaType,
    string BatchKey,
    string? WorkTitle = null);
